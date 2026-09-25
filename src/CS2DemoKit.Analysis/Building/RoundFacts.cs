namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     The round facts under <c>round.bomb.*</c> that are not trigger-driven context rules: the
///     site a round's bomb was planted at, read once at the plant. The single source of truth for
///     their v2 names, v1 node ids and types, consumed by the runtime (RuleChainBuilder builds the
///     nodes under these ids when a ruleset reads one) and by the catalog generator (which lists
///     them as contexts), the same arrangement as <see cref="B6RuleIds" />.
/// </summary>
public static class RoundFactIds
{
    /// <summary><c>round.bomb.site</c>: <c>"A"</c> or <c>"B"</c> once the bomb is planted this round, else <c>""</c>.</summary>
    public const string BombSite = "round_bomb_site";

    /// <summary><c>round.bomb.plant_place</c>: the planter's nav-mesh place at the plant, else <c>""</c>.</summary>
    public const string BombPlantPlace = "round_bomb_plant_place";

    /// <summary><c>round.bomb.site_entity</c>: <c>bomb_planted.Site</c>, the bomb target's entity index, else <c>-1</c>.</summary>
    public const string BombSiteEntity = "round_bomb_site_entity";

    /// <summary>The members, in catalog order: v2 dotted name, v1 node id, value type.</summary>
    public static IReadOnlyList<RoundFact> Members { get; } =
    [
        new("round.bomb.site", BombSite, "string"),
        new("round.bomb.plant_place", BombPlantPlace, "string"),
        new("round.bomb.site_entity", BombSiteEntity, "int")
    ];

    /// <summary>One round fact: its author-facing path, its node id, and its friendly value type.</summary>
    /// <param name="V2Name">The v2 dotted path (<c>round.bomb.site</c>).</param>
    /// <param name="RuleId">The node id the builder registers it under (<c>round_bomb_site</c>).</param>
    /// <param name="ValueType">The friendly value type: <c>string</c> or <c>int</c>.</param>
    public readonly record struct RoundFact(string V2Name, string RuleId, string ValueType);
}
