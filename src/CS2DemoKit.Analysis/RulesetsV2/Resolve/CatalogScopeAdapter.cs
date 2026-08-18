#region

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
///         <item><c>event.*</c> per wire event (its fields + the injected <c>event.tick</c> instant);</item>
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

    private readonly Dictionary<string, CatalogEnrichment> _enrichmentsByName;

    private readonly Dictionary<string, IScopeSymbol> _eventNamespaces;
    private readonly Dictionary<string, IScopeSymbol> _netMessageNamespaces;
    private readonly IReadOnlyList<IScopeSymbol> _roleMembers;

    private CatalogScopeAdapter(
        CatalogRoot catalog,
        IScopeSymbol player,
        IScopeSymbol round,
        IScopeSymbol match,
        IScopeSymbol enrich,
        Dictionary<string, IScopeSymbol> eventNamespaces,
        Dictionary<string, IScopeSymbol> netMessageNamespaces,
        IReadOnlyList<IScopeSymbol> roleMembers,
        Dictionary<string, CatalogEnrichment> enrichmentsByName)
    {
        Catalog = catalog;
        Player = player;
        Round = round;
        Match = match;
        Enrich = enrich;
        _eventNamespaces = eventNamespaces;
        _netMessageNamespaces = netMessageNamespaces;
        _roleMembers = roleMembers;
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

        IScopeSymbol player = tree.Build("player");
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

        return new CatalogScopeAdapter(catalog, player, round, match, enrich, eventNamespaces,
            netMessageNamespaces, roleMembers, enrichmentsByName);
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
        List<IScopeSymbol> members = [ScopeSymbol.Value("tick", RulesType.Instant)];
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
