namespace CS2DemoKit.Analysis.Catalog;

// The committed rules/catalog.json shape. One generated
// Catalog feeds the JSON schema, the expression checker, the in-app data browser, and the
// reference docs — nothing about the data surface is hand-maintained twice. Serialization is
// deterministic: every list is ordinally sorted by the generator, no timestamps, so the CI
// drift test can byte-compare a regen against the committed file.

/// <summary>Root of <c>rules/catalog.json</c>.</summary>
/// <param name="CatalogVersion">Catalog format version; bumped on breaking shape changes.</param>
/// <param name="Generator">Tool that produced the file.</param>
/// <param name="Events">Registered game events (wire + synthesized).</param>
/// <param name="NetMessages">Registered net-message trigger payloads.</param>
/// <param name="Enrichments">The <c>enrich.*</c> transient-node vocabulary.</param>
/// <param name="Contexts">Built-in context rules (round_number, bomb_status, …).</param>
/// <param name="Providers">Entity-value providers readable from rules.</param>
/// <param name="Profiles">Demo-source profiles with their logical-event bindings.</param>
/// <param name="Views">
///     Curated v2 author-facing views (kill, damage_dealt, round_won, …) — generated from
///     <c>tools/CS2DemoKit.RulesCatalog/data/views.yaml</c>. The v2 compiler's scope-env
///     adapter consumes this family; v1 has no views.
/// </param>
public sealed record CatalogRoot(
    int CatalogVersion,
    string Generator,
    IReadOnlyList<CatalogEvent> Events,
    IReadOnlyList<CatalogNetMessage> NetMessages,
    IReadOnlyList<CatalogEnrichment> Enrichments,
    IReadOnlyList<CatalogContextRule> Contexts,
    IReadOnlyList<CatalogProvider> Providers,
    IReadOnlyList<CatalogProfile> Profiles,
    IReadOnlyList<CatalogView> Views);

/// <summary>One registered game event.</summary>
/// <param name="Name">CS2 wire name (e.g. <c>player_death</c>).</param>
/// <param name="ClrType">Decoded CLR type's simple name.</param>
/// <param name="Synthesized">
///     True for events the analysis layer synthesizes from entity state (no wire event exists —
///     e.g. molotov_thrown); their CLR types live in the Analysis assembly, wire events in the
///     Parser.
/// </param>
/// <param name="FrequencyClass">
///     Measured occurrence class over the reference demo corpus (<c>perMatch</c> / <c>perRound</c> /
///     <c>frequent</c> / <c>perTick</c>, or <c>unmeasured</c>) — high-frequency lints key on
///     this field, never on hardcoded names. Re-baselined explicitly via the
///     generator's <c>--measure</c> verb, never hand-tagged.
/// </param>
/// <param name="Fields">Readable payload fields.</param>
public sealed record CatalogEvent(
    string Name,
    string ClrType,
    bool Synthesized,
    string FrequencyClass,
    IReadOnlyList<CatalogField> Fields);

/// <summary>One registered net message.</summary>
/// <param name="Name">Protobuf payload class name (e.g. <c>CDemoFileHeader</c>).</param>
/// <param name="ClrType">CLR payload type's simple name.</param>
/// <param name="FrequencyClass">
///     Measured occurrence class over the reference demo corpus (see <see cref="CatalogEvent.FrequencyClass" />).
///     The cautionary number behind this field: the removed CNETMsg_Tick plugin cost 123K+ edge
///     evaluations per demo.
/// </param>
/// <param name="Fields">Readable payload fields (protobuf plumbing excluded).</param>
public sealed record CatalogNetMessage(
    string Name,
    string ClrType,
    string FrequencyClass,
    IReadOnlyList<CatalogField> Fields);

/// <summary>A readable field on an event or net-message payload.</summary>
/// <param name="Name">Property name as written in rule expressions (<c>event.Name</c>).</param>
/// <param name="Type">Friendly type name (<c>int</c>, <c>string</c>, …).</param>
/// <param name="V2Name">
///     The v2 namespace path (<c>event.&lt;Name&gt;</c>) for the scope-env adapter.
///     Populated for game-event fields; null for net-message fields (payload matching is
///     reserved).
/// </param>
/// <param name="V2Type">
///     The v2 <c>RulesType</c> (<c>Bool</c>/<c>Int</c>/<c>Float</c>/<c>String</c>) the friendly
///     type maps to. Null wherever <see cref="V2Name" /> is.
/// </param>
public sealed record CatalogField(string Name, string Type, string? V2Name = null, string? V2Type = null);

/// <summary>One enrichment node (v1's <c>enrich.*</c> namespace; v2's typed facets).</summary>
/// <param name="Name">Full node name (e.g. <c>enrich.kill.was_enemy_kill</c>).</param>
/// <param name="ValueType">Friendly value type (<c>bool</c>, <c>int</c>, …).</param>
/// <param name="Scope">The enrichment family — the segment after <c>enrich.</c> (kill, hurt, blind, …).</param>
public sealed record CatalogEnrichment(string Name, string ValueType, string Scope);

/// <summary>One built-in context rule (round_number, gameplay_phase, bomb_status, …).</summary>
/// <param name="ChainId">Declaring built-in chain id.</param>
/// <param name="ChainScope"><c>Game</c> or <c>PerPlayer</c>.</param>
/// <param name="RuleId">Rule id authors reference.</param>
/// <param name="RuleType">Rule kind (<c>Bool</c>, <c>Value</c>, …).</param>
/// <param name="ValueType">Declared value type for value rules, else null.</param>
/// <param name="ResetOnRound">True when the rule resets at round boundaries.</param>
/// <param name="Triggers">Trigger <c>on:</c> names (sorted).</param>
/// <param name="V2Name">
///     The v2 namespace path the context rule maps to — e.g.
///     <c>round_number → round.number</c>, <c>gameplay_phase → match.phase</c>,
///     <c>alive → player.alive</c>. Every context rule must have a mapping (the generator errors
///     on an unmapped id), so this is never null in a valid catalog.
/// </param>
/// <param name="V2Type">The v2 <c>RulesType</c> (Bool → <c>Bool</c>, Counter → <c>Int</c>, Value → its value type).</param>
public sealed record CatalogContextRule(
    string ChainId,
    string ChainScope,
    string RuleId,
    string RuleType,
    string? ValueType,
    bool ResetOnRound,
    IReadOnlyList<string> Triggers,
    string? V2Name = null,
    string? V2Type = null);

/// <summary>One entity-value provider (v1: hand-written C#; the data-driven form makes these catalog data).</summary>
/// <param name="Name">Name rules read (e.g. <c>player.health</c> / <c>entity.game.freeze_period</c>).</param>
/// <param name="Scope"><c>perPlayer</c> or <c>singleton</c>.</param>
/// <param name="ClrType">Friendly value type name.</param>
/// <param name="V2Name">
///     The v2 namespace path: <c>entity.pawn.* → player.*</c>,
///     <c>entity.game.* → match.*</c>. Every provider must map (the generator errors otherwise),
///     so this is never null in a valid catalog.
/// </param>
/// <param name="V2Type">The v2 <c>RulesType</c> the friendly value type maps to.</param>
public sealed record CatalogProvider(
    string Name,
    string Scope,
    string ClrType,
    string? V2Name = null,
    string? V2Type = null);

/// <summary>One demo-source profile and its logical-event bindings (the per-source availability matrix).</summary>
/// <param name="Id">Profile type name (e.g. <c>Cs2GotvProfile</c>).</param>
/// <param name="Kind">The profile's demo-source kind.</param>
/// <param name="Bindings">Logical-event bindings the profile supplies.</param>
public sealed record CatalogProfile(
    string Id,
    string Kind,
    IReadOnlyList<CatalogBinding> Bindings);

/// <summary>One logical-event binding on a profile.</summary>
/// <param name="LogicalName">The author-facing <c>$logical</c> spelling (e.g. <c>$round_end</c>).</param>
/// <param name="Semantics"><c>FirstWins</c> or <c>AllFire</c>.</param>
/// <param name="ConcreteEvents">Ordered concrete event names the logical name resolves to.</param>
public sealed record CatalogBinding(
    string LogicalName,
    string Semantics,
    IReadOnlyList<string> ConcreteEvents);

/// <summary>
///     One curated v2 view — the author-facing verb (<c>kill</c>,
///     <c>damage_dealt</c>, <c>round_won</c>) a v2 ruleset triggers <c>on:</c>. Curated in
///     <c>views.yaml</c>, verified + resolved by the generator.
/// </summary>
/// <param name="Name">The view name authors write (<c>kill</c>).</param>
/// <param name="Event">
///     The logical event key (the <c>$name</c> binding, sans <c>$</c>) the view fires on;
///     resolved to concrete wire events per profile in <see cref="Profiles" />.
/// </param>
/// <param name="Binding">Attribution mode: <c>actor_slot</c> | <c>team</c> | <c>none</c>.</param>
/// <param name="Actor">The role whose slot binds to the ruleset player; set iff <c>binding: actor_slot</c>.</param>
/// <param name="Result">
///     For <c>binding: team</c> only: <c>won</c> (player's live team == round winner) or
///     <c>lost</c> (!= winner, gated by has_winner). Null otherwise.
/// </param>
/// <param name="Roles">Role → event slot-field map (killer → Attacker, …); readable as B5 handles.</param>
/// <param name="Baked">Editorial filters always applied — v2 expressions over <c>event.*</c>.</param>
/// <param name="Facets">Typed match-able attributes (<c>enemy</c>, <c>weapon</c>, …).</param>
/// <param name="Availability">Authored availability annotation (<c>all</c> or an explicit profile list).</param>
/// <param name="Profiles">Per-demo-source resolution of <see cref="Event" /> to concrete wire events.</param>
public sealed record CatalogView(
    string Name,
    string Event,
    string Binding,
    string? Actor,
    string? Result,
    IReadOnlyList<CatalogViewRole> Roles,
    IReadOnlyList<string> Baked,
    IReadOnlyList<CatalogFacet> Facets,
    string Availability,
    IReadOnlyList<CatalogViewProfile> Profiles);

/// <summary>A view role bound to an event slot-field.</summary>
/// <param name="Role">Role name (<c>killer</c>, <c>victim</c>, <c>planter</c>, …).</param>
/// <param name="Field">The event slot-field the role reads (<c>Attacker</c>).</param>
public sealed record CatalogViewRole(string Role, string Field);

/// <summary>
///     One view facet — a typed attribute an author can match on. Exactly one lowering form is
///     set (<see cref="Field" /> | <see cref="Enrichment" /> | <see cref="Expr" />) — a closed
///     set.
/// </summary>
/// <param name="Name">Facet name authors write (<c>enemy</c>, <c>weapon</c>).</param>
/// <param name="Type">Author-facing friendly type (<c>bool</c>, <c>int</c>, <c>float</c>, <c>string</c>).</param>
/// <param name="V2Type">The v2 <c>RulesType</c> the facet resolves to; verified against the field/enrichment's real type.</param>
/// <param name="Field">Event field this facet reads, or null.</param>
/// <param name="Enrichment">The <c>enrich.*</c> node this facet reads, or null.</param>
/// <param name="Expr">A v2 expression over <c>event.*</c> + roles, or null.</param>
public sealed record CatalogFacet(
    string Name,
    string Type,
    string V2Type,
    string? Field = null,
    string? Enrichment = null,
    string? Expr = null);

/// <summary>Per-profile resolution of a view's logical event to concrete wire events.</summary>
/// <param name="Profile">Profile id (<c>Cs2GotvProfile</c>).</param>
/// <param name="ConcreteEvents">
///     Concrete wire events the view fires on for this source; empty = the view is unavailable
///     there (drives coverage diagnostics).
/// </param>
public sealed record CatalogViewProfile(string Profile, IReadOnlyList<string> ConcreteEvents);
