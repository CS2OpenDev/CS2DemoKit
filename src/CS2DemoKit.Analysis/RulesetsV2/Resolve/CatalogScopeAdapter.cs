#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.Rules.Scopes;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The Catalog → scope-environment adapter: it maps the generated
///     <see cref="CatalogRoot" /> into the <see cref="IScopeSymbol" /> namespace trees the semantic
///     core resolves references against (spec §4), and injects the loader-provided symbols that have
///     no catalog entry. Concretely it builds:
///     <list type="bullet">
///         <item><c>player.*</c> from per-player providers + player contexts + <c>slot</c>/<c>team</c>/<c>name</c>;</item>
///         <item>
///             <c>round.*</c>/<c>match.*</c> from contexts + providers, plus the injected instants and the injected
///             sticky <c>round.bomb.was_planted</c>;
///         </item>
///         <item><c>enrich.*</c> from the enrichment family;</item>
///         <item>
///             <c>event.*</c> per wire event (its fields + the injected <c>event.tick</c> and
///             <c>event.frame_tick</c> instants);
///         </item>
///         <item>role-handle members (the per-player provider set) for B5 <c>victim.*</c>/<c>killer.*</c>/… reads.</item>
///     </list>
///     Every type is derived through <see cref="FriendlyTypeMap" />, so an unmapped friendly type is
///     a loud build error. The resolver assembles per-slot environments from these
///     shared trees plus the document's own siblings/params/<c>this</c>.
/// </summary>
public sealed class CatalogScopeAdapter
{
    /// <summary>The injected sticky per-round bomb-planted gate path.</summary>
    public const string BombWasPlantedPath = "round.bomb.was_planted";

    /// <summary>
    ///     The injected <c>event.frame_tick</c> member: the event's frame-clock tick
    ///     (<c>GameEvent.GameTick</c>), on every event, wire or synthesized.
    /// </summary>
    public const string FrameTickMember = "frame_tick";

    /// <summary>
    ///     The injected <c>team.side</c> member of a <c>for: each_team</c> ruleset: the side the
    ///     instance is for, 2 (T) or 3 (CT). A constant per instance; the planner substitutes it.
    /// </summary>
    public const string TeamSidePath = "team.side";

    /// <summary>
    ///     The <c>thrower</c> role of a <c>for: each_projectile</c> ruleset: the per-player provider
    ///     set read at the thrower's slot, in every expression slot, not only on a view that names it.
    /// </summary>
    public const string ThrowerRole = "thrower";

    /// <summary>The root of the per-projectile reads a <c>for: each_projectile</c> ruleset sees.</summary>
    public const string ProjectileRoot = "projectile";

    /// <summary>
    ///     The <c>projectile.*</c> members, each a field of the instance's
    ///     <see cref="Observation.ProjectileRow" /> as the frame it is read on holds it: <c>class</c>
    ///     is the short grenade name the views' <c>class</c> facet matches (<c>smoke</c>,
    ///     <c>molotov</c>, ...), <c>class_name</c> the server class, <c>thrower</c> the thrower's slot
    ///     held from its first resolution, <c>pos_*</c> the live position, <c>initial_*</c> where it
    ///     left the hand and <c>velocity_*</c> the release velocity. A position component reads NaN
    ///     while the position is unreadable; <c>is_incendiary</c> reads false off any class but a molotov;
    ///     <c>explode_effect_tick</c> reads 0 until the grenade detonates, and when unseen;
    ///     <c>first_seen_tick</c> is the frame-clock tick the projectile appeared on, which is the
    ///     created tick unless <c>projectile_created</c> waited for the thrower;
    ///     <c>smoke_detonation_*</c> is a smoke's <c>m_vSmokeDetonationPos</c>, NaN until it pops and
    ///     on every other class; <c>last_moved_tick</c> is the frame-clock tick the position last
    ///     changed by more than 0.1 units between two frames, the spawn until it first does.
    /// </summary>
    public static IReadOnlyList<(string Member, RulesType Type)> ProjectileMembers { get; } =
    [
        ("class", RulesType.String),
        ("class_name", RulesType.String),
        ("thrower", RulesType.Int),
        ("team", RulesType.Int),
        ("entity_index", RulesType.Int),
        ("serial", RulesType.Int),
        ("pos_x", RulesType.Float),
        ("pos_y", RulesType.Float),
        ("pos_z", RulesType.Float),
        ("initial_x", RulesType.Float),
        ("initial_y", RulesType.Float),
        ("initial_z", RulesType.Float),
        ("velocity_x", RulesType.Float),
        ("velocity_y", RulesType.Float),
        ("velocity_z", RulesType.Float),
        ("bounces", RulesType.Int),
        ("first_seen_tick", RulesType.Int),
        ("last_moved_tick", RulesType.Int),
        ("smoke_effect_tick", RulesType.Int),
        ("smoke_detonation_x", RulesType.Float),
        ("smoke_detonation_y", RulesType.Float),
        ("smoke_detonation_z", RulesType.Float),
        ("is_incendiary", RulesType.Bool),
        ("explode_effect_tick", RulesType.Int)
    ];

    private readonly Dictionary<string, CatalogEnrichment> _enrichmentsByName;

    private readonly Dictionary<string, IScopeSymbol> _eventNamespaces;
    private readonly Dictionary<string, IScopeSymbol> _netMessageNamespaces;
    private readonly IReadOnlyList<IScopeSymbol> _roleMembers;
    private readonly IReadOnlyList<IScopeSymbol> _commandMembers;

    private CatalogScopeAdapter(
        CatalogRoot catalog,
        IScopeSymbol player,
        IScopeSymbol round,
        IScopeSymbol match,
        IScopeSymbol enrich,
        IScopeSymbol team,
        IScopeSymbol projectile,
        Dictionary<string, IScopeSymbol> eventNamespaces,
        Dictionary<string, IScopeSymbol> netMessageNamespaces,
        IReadOnlyList<IScopeSymbol> roleMembers,
        IReadOnlyList<IScopeSymbol> commandMembers,
        Dictionary<string, CatalogEnrichment> enrichmentsByName)
    {
        Catalog = catalog;
        Player = player;
        Round = round;
        Match = match;
        Enrich = enrich;
        Team = team;
        Projectile = projectile;
        _eventNamespaces = eventNamespaces;
        _netMessageNamespaces = netMessageNamespaces;
        _roleMembers = roleMembers;
        _commandMembers = commandMembers;
        _enrichmentsByName = enrichmentsByName;
    }

    /// <summary>The catalog this adapter was built from (views/profiles are read by the resolver).</summary>
    public CatalogRoot Catalog { get; }

    /// <summary>The <c>player.*</c> root: per-player providers + player contexts + <c>slot</c>/<c>team</c>/<c>name</c>.</summary>
    public IScopeSymbol Player { get; }

    /// <summary>The <c>round.*</c> root: round contexts + the injected sticky <c>round.bomb.was_planted</c>.</summary>
    public IScopeSymbol Round { get; }

    /// <summary>The <c>match.*</c> root: match contexts/providers + the injected <c>match.tick</c> instant.</summary>
    public IScopeSymbol Match { get; }

    /// <summary>The <c>enrich.*</c> root: the enrichment family as a nested namespace tree.</summary>
    public IScopeSymbol Enrich { get; }

    /// <summary>
    ///     The <c>team.*</c> root, in scope only for a <c>for: each_team</c> ruleset: <c>team.side</c>,
    ///     the side the instance is for, and the per-side providers (<c>team.score</c>, ...).
    /// </summary>
    public IScopeSymbol Team { get; }

    /// <summary>
    ///     The <c>projectile.*</c> root, in scope only for a <c>for: each_projectile</c> ruleset:
    ///     <see cref="ProjectileMembers" />, read off the instance's row.
    /// </summary>
    public IScopeSymbol Projectile { get; }

    /// <summary>Builds an adapter from the embedded catalog (<see cref="CatalogResource.Load" />).</summary>
    /// <returns>The adapter.</returns>
    public static CatalogScopeAdapter FromEmbeddedCatalog() => From(CatalogResource.Load());

    /// <summary>Builds an adapter from a catalog root.</summary>
    /// <param name="catalog">The generated catalog.</param>
    /// <returns>The adapter.</returns>
    /// <exception cref="InvalidOperationException">A friendly type is unmapped (via <see cref="FriendlyTypeMap" />).</exception>
    public static CatalogScopeAdapter From(CatalogRoot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        NamespaceTreeBuilder tree = new();

        // Per-player providers double as role-handle members (spec §4: "the member set after the
        // role is exactly the per-player provider set").
        List<IScopeSymbol> roleMembers = [];
        // The command providers are the thrower's too (player.cmd.* as thrower.cmd.*), since the
        // thrower's slot is fixed per instance like the ruleset player's; a role read per fire is not.
        List<IScopeSymbol> commandMembers = [];
        foreach (CatalogProvider provider in catalog.Providers)
        {
            RulesType type = FriendlyTypeMap.Map(provider.ClrType);
            string path = provider.V2Name
                          ?? throw new InvalidOperationException(
                              $"catalog provider '{provider.Name}' has no v2Name — the generator must map every provider.");
            tree.Add(path, type);
            if (string.Equals(provider.Scope, "perPlayer", StringComparison.Ordinal))
            {
                roleMembers.Add(ScopeSymbol.Value(LastSegment(path), type));
            }
            else if (string.Equals(provider.Scope, "perPlayerCommand", StringComparison.Ordinal))
            {
                commandMembers.Add(ScopeSymbol.Value(LastSegment(path), type));
            }
        }

        foreach (CatalogContextRule context in catalog.Contexts)
        {
            RulesType type = ContextType(context);
            string path = context.V2Name
                          ?? throw new InvalidOperationException(
                              $"catalog context '{context.RuleId}' has no v2Name — the generator must map every context.");
            tree.Add(path, type);
        }

        // Loader-injected symbols with no catalog entry.
        tree.Add("player.slot", RulesType.Int);
        tree.Add("player.team", RulesType.Int);
        tree.Add("player.name", RulesType.String);
        tree.Add("match.tick", RulesType.Instant); // instant — no catalog event field
        tree.AddIfAbsent(BombWasPlantedPath, RulesType.Bool); // sticky; the catalog gains it later
        tree.Add(TeamSidePath, RulesType.Int);
        foreach ((string member, RulesType type) in ProjectileMembers)
        {
            tree.Add($"{ProjectileRoot}.{member}", type);
        }

        IScopeSymbol player = tree.Build("player");
        IScopeSymbol team = tree.Build("team");
        IScopeSymbol projectile = tree.Build(ProjectileRoot);
        IScopeSymbol round = tree.Build("round");
        IScopeSymbol match = tree.Build("match");

        // enrich.* nested tree.
        NamespaceTreeBuilder enrichTree = new();
        Dictionary<string, CatalogEnrichment> enrichmentsByName = new(StringComparer.Ordinal);
        foreach (CatalogEnrichment enrichment in catalog.Enrichments)
        {
            enrichTree.Add(enrichment.Name, FriendlyTypeMap.Map(enrichment.ValueType));
            enrichmentsByName[enrichment.Name] = enrichment;
        }

        IScopeSymbol enrich = enrichTree.Build("enrich");

        // event.* namespaces, one per catalog event: its fields + the injected event.tick instant.
        Dictionary<string, IScopeSymbol> eventNamespaces = new(StringComparer.Ordinal);
        foreach (CatalogEvent gameEvent in catalog.Events)
        {
            eventNamespaces[gameEvent.Name] = BuildEventNamespaceCore(gameEvent.Fields);
        }

        // event.* namespaces for net-message triggers: a net.<Message> trigger's where:/match:
        // reads its payload fields under the same event.* spelling a game-event view uses (net
        // payload matching). Built exactly like a game event — the message's catalog
        // fields plus the injected event.tick instant.
        Dictionary<string, IScopeSymbol> netMessageNamespaces = new(StringComparer.Ordinal);
        foreach (CatalogNetMessage netMessage in catalog.NetMessages)
        {
            netMessageNamespaces[netMessage.Name] = BuildEventNamespaceCore(netMessage.Fields);
        }

        return new CatalogScopeAdapter(catalog, player, round, match, enrich, team, projectile, eventNamespaces,
            netMessageNamespaces, roleMembers, commandMembers, enrichmentsByName);
    }

    /// <summary>
    ///     The <c>event.*</c> namespace for a wire event: its catalog fields plus the injected
    ///     <c>event.tick</c> instant. Unknown events still expose <c>event.tick</c> (every event
    ///     carries a tick); their fields simply do not resolve.
    /// </summary>
    /// <param name="eventName">The wire event name (the view's logical event, or a <c>raw.&lt;event&gt;</c> name).</param>
    /// <returns>The <c>event</c> namespace symbol.</returns>
    public IScopeSymbol EventNamespace(string eventName)
    {
        ArgumentNullException.ThrowIfNull(eventName);
        return _eventNamespaces.TryGetValue(eventName, out IScopeSymbol? ns)
            ? ns
            : BuildEventNamespaceCore([]);
    }

    /// <summary>
    ///     The <c>event.*</c> namespace for a <c>net.&lt;Message&gt;</c> trigger: the message's
    ///     catalog payload fields plus the injected <c>event.tick</c> instant. An unknown message
    ///     still exposes <c>event.tick</c> (mirroring <see cref="EventNamespace" />); its fields
    ///     simply do not resolve, so a <c>where:</c> over them reports an attributed unknown-field
    ///     error exactly like a game-event view field error.
    /// </summary>
    /// <param name="messageName">The net-message payload class name (the <c>net.&lt;Message&gt;</c> name).</param>
    /// <returns>The <c>event</c> namespace symbol.</returns>
    public IScopeSymbol NetMessageNamespace(string messageName)
    {
        ArgumentNullException.ThrowIfNull(messageName);
        return _netMessageNamespaces.TryGetValue(messageName, out IScopeSymbol? ns)
            ? ns
            : BuildEventNamespaceCore([]);
    }

    /// <summary>A role-handle namespace (<c>victim</c>, <c>killer</c>, …) exposing the per-player provider set (B5).</summary>
    /// <param name="role">The role name.</param>
    /// <returns>The role namespace symbol.</returns>
    public IScopeSymbol RoleNamespace(string role)
    {
        ArgumentNullException.ThrowIfNull(role);
        return ScopeSymbol.Namespace(role, [.. _roleMembers]);
    }

    /// <summary>
    ///     The <c>thrower</c> namespace of a <c>for: each_projectile</c> ruleset: the per-player
    ///     provider set, plus <c>cmd.*</c> for the command providers, both at the instance's slot.
    /// </summary>
    /// <returns>The namespace symbol.</returns>
    public IScopeSymbol ThrowerNamespace() =>
        ScopeSymbol.Namespace(ThrowerRole, [.. _roleMembers, ScopeSymbol.Namespace(CommandMember, [.. _commandMembers])]);

    /// <summary>The segment under <c>player</c> and <c>thrower</c> that holds the command providers.</summary>
    public const string CommandMember = "cmd";

    /// <summary>
    ///     Lowers a view facet to the underlying read expression it desugars to (spec §5 row 5):
    ///     <c>field:</c> → <c>event.&lt;Field&gt;</c>, <c>enrichment:</c> → the <c>enrich.*</c>
    ///     reference, <c>expr:</c> → the parsed expression over <c>event.*</c> + roles. Both the
    ///     <c>match:</c> lowering and a free-form <c>where:</c> read of the facet name inline through
    ///     this same read, so structured and free-form spellings hash identically.
    /// </summary>
    /// <param name="facet">The catalog facet.</param>
    /// <returns>The read expression the facet desugars to.</returns>
    /// <exception cref="InvalidOperationException">The facet sets none of the three closed lowering forms.</exception>
    public ExpressionNode FacetRead(CatalogFacet facet)
    {
        ArgumentNullException.ThrowIfNull(facet);
        if (facet.Field is { } field)
        {
            return new ReferenceNode(["event", field]);
        }

        if (facet.Enrichment is { } enrichment)
        {
            if (!_enrichmentsByName.ContainsKey(enrichment))
            {
                throw new InvalidOperationException(
                    $"facet '{facet.Name}' reads enrichment '{enrichment}', which is not in the catalog enrichment family.");
            }

            return ReferenceNode.FromPath(enrichment);
        }

        if (facet.Expr is { } expr)
        {
            LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(expr);
            return parsed.Success
                ? parsed.Require()
                : throw new InvalidOperationException(
                    $"facet '{facet.Name}' has an unparseable expr: '{expr}' — {parsed.Diagnostics[0].Message}");
        }

        throw new InvalidOperationException(
            $"facet '{facet.Name}' sets none of field/enrichment/expr — the generator must set exactly one.");
    }

    /// <summary>
    ///     Looks up an enrichment by its full node name (<c>enrich.shot.ticks_since_spot</c>). The
    ///     resolver reads the entry's sentinel declaration off it before it lets a stat aggregate the
    ///     node.
    /// </summary>
    /// <param name="name">The full <c>enrich.*</c> node name.</param>
    /// <param name="enrichment">The catalog entry when the name is a registered enrichment.</param>
    /// <returns>True when the name is in the catalog's enrichment family.</returns>
    public bool TryGetEnrichment(string name, [NotNullWhen(true)] out CatalogEnrichment? enrichment)
    {
        ArgumentNullException.ThrowIfNull(name);
        return _enrichmentsByName.TryGetValue(name, out enrichment);
    }

    /// <summary>The v2 type the facet resolves to (used for typing a free-form facet read symbol).</summary>
    /// <param name="facet">The catalog facet.</param>
    /// <returns>The facet's <see cref="RulesType" />.</returns>
    public static RulesType FacetType(CatalogFacet facet)
    {
        ArgumentNullException.ThrowIfNull(facet);
        return FriendlyTypeMap.Map(facet.Type);
    }

    private static ScopeSymbol BuildEventNamespaceCore(IReadOnlyList<CatalogField> fields)
    {
        // Two clocks, both injected: `tick` is the server clock a wire event is stamped with (the
        // frame clock on a synthesized event, which has no server stamp), `frame_tick` is always the
        // frame clock a DemoFrame, a timeline event and a highlight are indexed by.
        List<IScopeSymbol> members =
        [
            ScopeSymbol.Value("tick", RulesType.Instant),
            ScopeSymbol.Value(FrameTickMember, RulesType.Instant)
        ];
        foreach (CatalogField field in fields)
        {
            members.Add(ScopeSymbol.Value(field.Name, FriendlyTypeMap.Map(field.Type)));
        }

        return ScopeSymbol.Namespace("event", [.. members]);
    }

    private static RulesType ContextType(CatalogContextRule context) =>
        context.RuleType switch
        {
            "Bool" => RulesType.Bool,
            "Counter" => RulesType.Int,
            "Value" => FriendlyTypeMap.Map(context.ValueType
                                           ?? throw new InvalidOperationException(
                                               $"catalog context '{context.RuleId}' is a Value rule with no valueType.")),
            _ => FriendlyTypeMap.Map(context.ValueType
                                     ?? throw new InvalidOperationException(
                                         $"catalog context '{context.RuleId}' has ruleType '{context.RuleType}' and no valueType to map."))
        };

    private static string LastSegment(string path)
    {
        int dot = path.LastIndexOf('.');
        return dot < 0 ? path : path[(dot + 1)..];
    }
}
