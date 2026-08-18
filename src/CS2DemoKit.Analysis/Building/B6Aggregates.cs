namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     The single source of truth for the B6 team-aggregate namespaces
///     (<c>round.team.*</c> / <c>round.enemies.*</c> / <c>round.alive.*</c>): the mapping between each
///     v2 dotted member name and its v1 rule id, plus the member's rule type. Consumed by both the
///     runtime (RuleChainBuilder injects the backing per-player nodes under these v1 rule ids) and the
///     catalog generator (CatalogBuilder appends these as <c>contexts</c> entries so the member
///     resolves + types + lowers). Keeping the list here means the scope tree and the graph nodes
///     never drift apart.
/// </summary>
public static class B6RuleIds
{
    /// <summary><c>round.team.alive</c> — alive+connected players on the subject's team.</summary>
    public const string TeamAlive = "round_team_alive";

    /// <summary><c>round.team.players</c> — connected players on the subject's team.</summary>
    public const string TeamPlayers = "round_team_players";

    /// <summary><c>round.team.equipment</c> — freeze-end equipment sum for the subject's team.</summary>
    public const string TeamEquipment = "round_team_equipment";

    /// <summary><c>round.enemies.alive</c> — alive+connected players on the opposing team.</summary>
    public const string EnemiesAlive = "round_enemies_alive";

    /// <summary><c>round.enemies.players</c> — connected players on the opposing team.</summary>
    public const string EnemiesPlayers = "round_enemies_players";

    /// <summary><c>round.enemies.equipment</c> — freeze-end equipment sum for the opposing team.</summary>
    public const string EnemiesEquipment = "round_enemies_equipment";

    /// <summary><c>round.alive.in_clutch</c> — subject is the lone connected survivor in an active clutch.</summary>
    public const string AliveInClutch = "round_alive_in_clutch";

    /// <summary><c>round.clutch.size</c> — enemies alive when the subject entered their clutch (the N of a 1vN); 0 otherwise.</summary>
    public const string ClutchSize = "round_clutch_size";

    /// <summary>
    ///     The B6 member set (v2 dotted name → v1 rule id + rule type). <c>RuleType</c> uses the
    ///     catalog context-rule spelling: <c>"Counter"</c> (→ Int in the scope tree) for the integer
    ///     aggregates, <c>"Bool"</c> for the clutch facet. The digest-sampled economy members
    ///     (<c>round.team.equipment</c> / <c>round.enemies.equipment</c>) are appended alongside their
    ///     freeze-end maintenance edge so the catalog only ever exposes members the runtime can lower.
    /// </summary>
    public static IReadOnlyList<B6Member> Members { get; } =
    [
        new("round.team.alive", TeamAlive, "Counter"),
        new("round.team.players", TeamPlayers, "Counter"),
        new("round.team.equipment", TeamEquipment, "Counter"),
        new("round.enemies.alive", EnemiesAlive, "Counter"),
        new("round.enemies.players", EnemiesPlayers, "Counter"),
        new("round.enemies.equipment", EnemiesEquipment, "Counter"),
        new("round.alive.in_clutch", AliveInClutch, "Bool"),
        new("round.clutch.size", ClutchSize, "Counter")
    ];

    /// <summary>One B6 aggregate member: its v2 dotted name, its v1 rule id, and its context rule type.</summary>
    /// <param name="V2Name">The author-facing dotted path (e.g. <c>round.team.alive</c>).</param>
    /// <param name="RuleId">The v1 rule id the backing graph node is keyed under (e.g. <c>round_team_alive</c>).</param>
    /// <param name="RuleType">Catalog context rule type: <c>"Counter"</c> (Int) or <c>"Bool"</c>.</param>
    public readonly record struct B6Member(string V2Name, string RuleId, string RuleType);
}
