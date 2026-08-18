#region

using System.Reflection;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.RulesCatalog;

/// <summary>
///     Builds the <see cref="CatalogRoot" /> by enumerating the engine's own registries.
///     The rule: one generated Catalog feeds the JSON schema, the checker,
///     the data browser, and the docs, so the data surface is never hand-maintained twice.
///     Everything is sorted ordinally so output is deterministic (the CI drift test
///     byte-compares a regen against the committed file).
/// </summary>
public static class CatalogBuilder
{
    /// <summary>Catalog format version — bump on breaking shape changes.</summary>
    public const int Version = 1;

    /// <summary>
    ///     Built-in context rule id → v2 namespace path (compiler-plan §4.1). Explicit because the
    ///     round-vs-match split within the game-scoped chain is editorial, not derivable from
    ///     scope alone (e.g. round_active is game-scoped but a round concept). An unmapped id is a
    ///     generator error, forcing a deliberate choice when a context rule is added.
    /// </summary>
    private static readonly Dictionary<string, string> _contextV2Names = new(StringComparer.Ordinal)
    {
        // game-scoped, round-level concepts → round.*
        ["round_number"] = "round.number",
        ["round_active"] = "round.active",
        ["bomb_status"] = "round.bomb_status",
        ["bomb_was_planted"] = "round.bomb.was_planted",
        ["no_deaths_yet"] = "round.no_deaths_yet",
        // game-scoped, match-level concepts → match.*
        ["gameplay_phase"] = "match.phase",
        ["half_state"] = "match.half_state",
        ["map_name"] = "match.map",
        ["match_live"] = "match.live",
        ["regulation_status"] = "match.regulation_status",
        ["rounds_after_half_announce"] = "match.rounds_after_half_announce",
        // per-player context → player.*
        ["alive"] = "player.alive",
        ["survived"] = "player.survived",
        ["traded"] = "player.traded"
    };

    /// <summary>Builds the full catalog from the live registries.</summary>
    /// <param name="frequencies">
    ///     Measured trigger frequencies; null → every entry "unmeasured". Ordinary
    ///     regens pass the committed baseline so output stays deterministic.
    /// </param>
    public static CatalogRoot Build(FrequencyBaseline? frequencies = null)
    {
        EventRegistry registry = EventRegistry.Build();
        frequencies ??= new FrequencyBaseline();

        // Events, enrichments, and profiles are captured as locals because the v2 `views` family
        // is verified + resolved against all three (role/facet field checks, enrichment-name +
        // type checks, per-profile concrete-event resolution).
        List<CatalogEvent> events = BuildEvents(registry, frequencies);
        List<CatalogEnrichment> enrichments = BuildEnrichments(registry);
        List<CatalogProfile> profiles = BuildProfiles();

        return new CatalogRoot(
            Version,
            "CS2DemoKit.RulesCatalog",
            events,
            BuildNetMessages(registry, frequencies),
            enrichments,
            BuildContexts(),
            BuildProviders(),
            profiles,
            BuildViews(events, enrichments, profiles));
    }

    private static List<CatalogEvent> BuildEvents(EventRegistry registry, FrequencyBaseline frequencies) =>
        registry.EventNames
            .Select(name => registry.GetEvent(name)!)
            .Select(reg => new CatalogEvent(
                reg.Name,
                reg.EventType.Name,
                // Synthesized events (no wire event exists; produced by the analysis layer's
                // entity scanner) live in the Analysis assembly, wire events in the Parser.
                reg.EventType.Namespace?.StartsWith(
                    "CS2DemoKit.Analysis", StringComparison.Ordinal) == true,
                frequencies.Classify(reg.Name),
                BuildFields(reg.EventType, typeof(GameEvent), true)))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();

    private static List<CatalogNetMessage> BuildNetMessages(EventRegistry registry, FrequencyBaseline frequencies) =>
        registry.NetMessageNames
            .Select(name => registry.GetNetMessage(name)!)
            .Select(reg => new CatalogNetMessage(
                reg.Name,
                reg.PayloadType.Name,
                frequencies.Classify(reg.Name),
                // Net-message payload matching is reserved (compiler-plan §9) — no v2 adapter fields.
                BuildFields(reg.PayloadType, null, false)))
            .OrderBy(m => m.Name, StringComparer.Ordinal)
            .ToList();

    private static List<CatalogField> BuildFields(Type type, Type? skipDeclaredBy, bool adaptTypes)
    {
        List<CatalogField> fields = [];
        foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead
                || prop.DeclaringType == typeof(object)
                || skipDeclaredBy is not null && prop.DeclaringType == skipDeclaredBy
                // Protobuf plumbing on net-message payloads, not data fields.
                || prop.Name is "Descriptor" or "Parser")
            {
                continue;
            }

            string friendly = FriendlyTypeName(prop.PropertyType);
            // Game-event fields carry the v2 adapter (namespace path + RulesType); net-message
            // fields don't (payload matching reserved). An unmappable event-field type is a
            // generator error, not a silent skip (compiler-plan §4.1).
            fields.Add(adaptTypes
                ? new CatalogField(prop.Name, friendly, "event." + prop.Name, RulesType(friendly))
                : new CatalogField(prop.Name, friendly));
        }

        return fields.OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
    }

    private static List<CatalogEnrichment> BuildEnrichments(EventRegistry registry)
    {
        // Build the real enrichment infrastructure against a throwaway graph — the node set IS
        // the declaration (no hand-maintained list to drift). The GOTV profile is the vanilla
        // fallback; enrichment NODES are profile-independent (only edge counts vary by binding).
        StateGraph graph = new();
        BuiltinContexts.EnrichmentInfrastructure infra = BuiltinContexts.CreateEnrichment(
            graph.Root,
            new PlayerContextIndex(),
            registry,
            new LogicalEventResolver(new Cs2GotvProfile()));

        return infra.Nodes
            .Select(node => new CatalogEnrichment(
                node.Name,
                NodeValueType(node),
                node.Name.Split('.') is [_, var scope, ..] ? scope : ""))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static List<CatalogContextRule> BuildContexts() =>
        BuiltinContexts.GenerateContextRules()
            .SelectMany(chain => chain.Rules.Select(rule => new CatalogContextRule(
                chain.Id,
                chain.Scope.ToString(),
                rule.Id,
                rule.Type.ToString(),
                rule.ValueType,
                rule.ResetOnRound,
                (rule.Triggers ?? []).Select(t => t.On)
                .OrderBy(n => n, StringComparer.Ordinal).ToList(),
                ContextV2Name(rule.Id),
                ContextV2Type(rule.Type.ToString(), rule.ValueType))))
            .Concat(BuildB6AggregateContexts())
            .OrderBy(c => c.ChainId, StringComparer.Ordinal)
            .ThenBy(c => c.RuleId, StringComparer.Ordinal)
            .ToList();

    // B6 team aggregates (round.team.* / round.enemies.* / round.alive.*). These are not trigger-driven
    // RuleDefs — the runtime backs them with read-derived per-player nodes (RuleChainBuilder injects
    // them under the same v1 rule ids) — so they are appended here as hand-built context entries rather
    // than flowing from GenerateContextRules(). B6RuleIds is the shared source of truth for the
    // v2Name↔ruleId↔type mapping, so the scope tree, the graph nodes, and this catalog can't drift.
    private static IEnumerable<CatalogContextRule> BuildB6AggregateContexts() =>
        B6RuleIds.Members.Select(m => new CatalogContextRule(
            "_builtin_b6_aggregates",
            "PerPlayer",
            m.RuleId,
            m.RuleType,
            m.RuleType == "Counter" ? "int" : null,
            false,
            [],
            m.V2Name,
            ContextV2Type(m.RuleType, "int")));

    private static List<CatalogProvider> BuildProviders()
    {
        List<CatalogProvider> providers =
        [
            .. PerPlayerEntityValueProviderRegistry.CreateDefault().All
                .Select(p => new CatalogProvider(
                    p.Name, "perPlayer", FriendlyTypeName(p.ValueType),
                    ProviderV2Name(p.Name), RulesType(FriendlyTypeName(p.ValueType)))),
            .. EntityValueProviderRegistry.CreateDefault().All
                .Select(p => new CatalogProvider(
                    p.ContextName, "singleton", FriendlyTypeName(p.ValueType),
                    ProviderV2Name(p.ContextName), RulesType(FriendlyTypeName(p.ValueType))))
        ];

        return providers.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
    }

    private static List<CatalogProfile> BuildProfiles()
    {
        // The logical-binding properties on the profile base class ARE the availability matrix.
        PropertyInfo[] bindingProps = typeof(DemoSourceProfile)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(LogicalEventBinding))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToArray();

        List<CatalogProfile> profiles = [];
        // The profile IMPLEMENTATIONS live in the Analysis assembly (the abstract base is in
        // Abstractions) — anchor the scan on a known profile type's assembly.
        foreach (Type type in typeof(Cs2GotvProfile).Assembly.GetTypes()
                     .Where(t => t.IsSubclassOf(typeof(DemoSourceProfile)) && !t.IsAbstract)
                     .OrderBy(t => t.Name, StringComparer.Ordinal))
        {
            DemoSourceProfile profile = (DemoSourceProfile)Activator.CreateInstance(type)!;
            List<CatalogBinding> bindings = [];
            foreach (PropertyInfo prop in bindingProps)
            {
                if (prop.GetValue(profile) is LogicalEventBinding binding)
                {
                    bindings.Add(new CatalogBinding(
                        // The author-facing spelling: `$round_end` maps to RoundEnd via
                        // LogicalEventResolver's snake-caser; the catalog serves authoring
                        // tools, so it speaks the authoring form.
                        "$" + ToSnakeCase(prop.Name),
                        binding.Semantics.ToString(),
                        binding.ConcreteEventNames.ToList()));
                }
            }

            profiles.Add(new CatalogProfile(type.Name, ProfileKind(profile), bindings));
        }

        return profiles;
    }

    // Mirrors LogicalEventResolver's private snake-caser (RoundEnd → round_end). Kept local so
    // the generator needs no engine changes; the drift test catches divergence via the enum
    // checks in `rules check` if the resolver's convention ever moves.
    private static string ToSnakeCase(string pascal)
    {
        StringBuilder sb = new(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            char c = pascal[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static string ProfileKind(DemoSourceProfile profile) =>
        // Kind is a small enum-ish property on the profile; fall back to the type name when a
        // future profile shape changes it.
        typeof(DemoSourceProfile).GetProperty("Kind")?.GetValue(profile)?.ToString()
        ?? profile.GetType().Name;

    private static string NodeValueType(StateNode node)
    {
        // TransientBoolNode → bool; TransientValueNode<T> → T. Walk the type chain for the
        // first generic ValueNode<T> ancestor; plain bool nodes have none.
        for (Type? t = node.GetType(); t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueNode<>))
            {
                return FriendlyTypeName(t.GetGenericArguments()[0]);
            }
        }

        return "bool";
    }

    // ── v2 views family (compiler-plan §5) ───────────────────────────────────────
    //
    // Reads data/views.yaml, verifies every role slot-field and every field/enrichment facet
    // against the live registries, resolves each view's logical event to concrete wire events
    // per profile, and projects the result into the catalog. Any curation mistake (unknown
    // event/field/enrichment, type mismatch, malformed binding) is a loud generator error.
    private static List<CatalogView> BuildViews(
        IReadOnlyList<CatalogEvent> events,
        IReadOnlyList<CatalogEnrichment> enrichments,
        IReadOnlyList<CatalogProfile> profiles)
    {
        ViewsFile.ViewsDoc doc = ViewsFile.Load();

        Dictionary<string, CatalogEvent> eventByName =
            events.ToDictionary(e => e.Name, StringComparer.Ordinal);
        Dictionary<string, string> enrichmentType =
            enrichments.ToDictionary(e => e.Name, e => e.ValueType, StringComparer.Ordinal);

        List<CatalogView> views = [];
        foreach ((string name, ViewsFile.ViewDto dto) in doc.Views)
        {
            string binding = dto.Binding ?? throw ViewError(name, "missing 'binding:'");
            if (binding is not ("actor_slot" or "team" or "none"))
            {
                throw ViewError(name, $"binding '{binding}' is not one of actor_slot|team|none");
            }

            string eventKey = dto.Event ?? throw ViewError(name, "missing 'event:'");
            // The `event:` value also names the primary wire event; role/field-facet checks run
            // against its catalog fields.
            if (!eventByName.TryGetValue(eventKey, out CatalogEvent? evt))
            {
                throw ViewError(name, $"event '{eventKey}' is not a registered game event");
            }

            Dictionary<string, string> eventFields =
                evt.Fields.ToDictionary(f => f.Name, f => f.Type, StringComparer.Ordinal);

            switch (binding)
            {
                case "actor_slot":
                    if (dto.Actor is null)
                    {
                        throw ViewError(name, "binding: actor_slot requires 'actor:'");
                    }

                    if (dto.Result is not null)
                    {
                        throw ViewError(name, "'result:' is only valid for binding: team");
                    }

                    break;
                case "team":
                    if (dto.Result is not ("won" or "lost"))
                    {
                        throw ViewError(name, "binding: team requires 'result:' of won|lost");
                    }

                    if (dto.Actor is not null)
                    {
                        throw ViewError(name, "'actor:' is only valid for binding: actor_slot");
                    }

                    break;
                default: // none
                    if (dto.Actor is not null)
                    {
                        throw ViewError(name, "'actor:' is only valid for binding: actor_slot");
                    }

                    if (dto.Result is not null)
                    {
                        throw ViewError(name, "'result:' is only valid for binding: team");
                    }

                    break;
            }

            List<CatalogViewRole> roles = [];
            foreach ((string role, string field) in dto.Roles.OrderBy(r => r.Key, StringComparer.Ordinal))
            {
                if (!eventFields.ContainsKey(field))
                {
                    throw ViewError(name, $"role '{role}' → field '{field}' is not a field of event '{eventKey}'");
                }

                roles.Add(new CatalogViewRole(role, field));
            }

            if (binding == "actor_slot" && !dto.Roles.ContainsKey(dto.Actor!))
            {
                throw ViewError(name, $"actor '{dto.Actor}' is not one of the declared roles");
            }

            List<CatalogFacet> facets = [];
            foreach ((string fname, ViewsFile.FacetDto f) in dto.Facets.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                string type = f.Type ?? throw ViewError(name, $"facet '{fname}' missing 'type:'");
                string v2Type = RulesType(type);
                int forms = (f.Field is not null ? 1 : 0)
                            + (f.Enrichment is not null ? 1 : 0)
                            + (f.Expr is not null ? 1 : 0);
                if (forms != 1)
                {
                    throw ViewError(name, $"facet '{fname}' must set exactly one of field|enrichment|expr (has {forms})");
                }

                if (f.Field is not null)
                {
                    if (!eventFields.TryGetValue(f.Field, out string? ft))
                    {
                        throw ViewError(name, $"facet '{fname}' → field '{f.Field}' is not a field of event '{eventKey}'");
                    }

                    if (RulesType(ft) != v2Type)
                    {
                        throw ViewError(name, $"facet '{fname}' declares type '{type}' but field '{f.Field}' is '{ft}'");
                    }
                }
                else if (f.Enrichment is not null)
                {
                    if (!enrichmentType.TryGetValue(f.Enrichment, out string? et))
                    {
                        throw ViewError(name, $"facet '{fname}' → enrichment '{f.Enrichment}' is not a registered enrichment");
                    }

                    if (RulesType(et) != v2Type)
                    {
                        throw ViewError(name, $"facet '{fname}' declares type '{type}' but enrichment '{f.Enrichment}' is '{et}'");
                    }
                }
                // expr: author-declared type, verified by the v2 checker (track C), not here.

                facets.Add(new CatalogFacet(fname, type, v2Type, f.Field, f.Enrichment, f.Expr));
            }

            // Per-profile concrete-event resolution from the profiles family: the logical key is the
            // view's `logical:` when set, else its `event:` (the wire event doubles as the logical key
            // by default — every view without `logical:` is byte-identical). Decoupling is needed when
            // a profile binds the logical concept under a key that differs from the wire name (e.g.
            // he_grenade's wire `hegrenade_detonate` vs logical `$he_grenade_detonate`).
            // An empty concrete list on a profile = the view is unavailable there (coverage skip).
            string logical = "$" + (dto.Logical ?? eventKey);
            List<CatalogViewProfile> perProfile = [];
            bool anyBound = false;
            foreach (CatalogProfile profile in profiles)
            {
                IReadOnlyList<string> concrete =
                    profile.Bindings.FirstOrDefault(x => x.LogicalName == logical)?.ConcreteEvents ?? [];
                if (concrete.Count > 0)
                {
                    anyBound = true;
                }

                perProfile.Add(new CatalogViewProfile(profile.Id, concrete));
            }

            if (!anyBound)
            {
                throw ViewError(name, $"logical event '{logical}' is not bound on any profile");
            }

            views.Add(new CatalogView(
                name, eventKey, binding, dto.Actor, dto.Result,
                roles, dto.Baked, facets, dto.Availability ?? "all", perProfile));
        }

        return views.OrderBy(v => v.Name, StringComparer.Ordinal).ToList();
    }

    private static InvalidOperationException ViewError(string view, string message) =>
        new($"views.yaml: view '{view}': {message}");

    // ── v2 scope-env adapter data (compiler-plan §4.1) ───────────────────────────

    /// <summary>
    ///     Friendly catalog type → v2 <c>RulesType</c>. There is no unsigned v2 type (spec §3.2),
    ///     so uint/ulong widen to <c>Int</c>. An unmapped type is a generator error, never a
    ///     silent skip.
    /// </summary>
    private static string RulesType(string friendly) => friendly switch
    {
        "bool" => "Bool",
        // All integral widths collapse to Int: v2 has one integer type (spec §3.2), and the
        // narrow ones only appear because the SDK preserves the wire's KV1 width.
        "int" or "long" or "uint" or "ulong"
            or "byte" or "sbyte" or "short" or "ushort" => "Int",
        "float" or "double" => "Float",
        "string" => "String",
        _ => throw new InvalidOperationException(
            $"catalog v2 adapter: no RulesType for friendly type '{friendly}' — extend the map "
            + "or exclude the field (compiler-plan §4.1)")
    };

    /// <summary>Provider name → v2 namespace path: <c>entity.pawn.* → player.*</c>, <c>entity.game.* → match.*</c>.</summary>
    private static string ProviderV2Name(string name) =>
        name.StartsWith("entity.pawn.", StringComparison.Ordinal)
            ? "player." + name["entity.pawn.".Length..]
            : name.StartsWith("entity.game.", StringComparison.Ordinal)
                ? "match." + name["entity.game.".Length..]
                : throw new InvalidOperationException(
                    $"catalog v2 adapter: no v2 namespace mapping for provider '{name}' "
                    + "(expected entity.pawn.* or entity.game.*)");

    private static string ContextV2Name(string ruleId) =>
        _contextV2Names.TryGetValue(ruleId, out string? v2)
            ? v2
            : throw new InvalidOperationException(
                $"catalog v2 adapter: no v2 namespace mapping for context rule '{ruleId}' — "
                + "add it to ContextV2Names (compiler-plan §4.1)");

    private static string ContextV2Type(string ruleType, string? valueType) => ruleType switch
    {
        "Bool" => "Bool",
        "Counter" => "Int",
        "Value" => RulesType(valueType
                             ?? throw new InvalidOperationException(
                                 "catalog v2 adapter: Value context rule has no valueType")),
        _ => throw new InvalidOperationException(
            $"catalog v2 adapter: no v2 type for context rule kind '{ruleType}'")
    };

    private static string FriendlyTypeName(Type type) => type switch
    {
        _ when type == typeof(int) => "int",
        // The SDK's event records type each field to its KV1 tag rather than widening everything
        // to int the way the retired generator did, so byte/sbyte/short/ushort reach here now.
        _ when type == typeof(byte) => "byte",
        _ when type == typeof(sbyte) => "sbyte",
        _ when type == typeof(short) => "short",
        _ when type == typeof(ushort) => "ushort",
        _ when type == typeof(long) => "long",
        _ when type == typeof(ulong) => "ulong",
        _ when type == typeof(uint) => "uint",
        _ when type == typeof(float) => "float",
        _ when type == typeof(double) => "double",
        _ when type == typeof(bool) => "bool",
        _ when type == typeof(string) => "string",
        _ => type.Name
    };
}
