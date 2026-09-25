#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Writes the round's plant-site facts on <c>bomb_planted</c>: the bombsite letter, the planter's
///     nav-mesh place and the bomb target's entity index. <c>bomb_planted.Site</c> is an entity index
///     (248 on the sample, 173 / 236 on nuke, 96 / 97 on the build-10924 dust2 demo), not a letter, so
///     the letter comes from where the planter stood: their pawn's <c>m_szLastPlaceName</c> reads
///     <c>BombsiteA</c> or <c>BombsiteB</c>, and it agreed with the planted C4's own
///     <c>m_nBombSite</c> on every plant measured. The place is the pre-frame read, the state before
///     the plant frame; a planter does not move during the plant.
///     <para>
///         The three nodes are round-scoped: they reset at the next freeze end, so they survive to
///         the round's close and to a round table.
///     </para>
/// </summary>
/// <param name="source">The edge source (the graph root).</param>
/// <param name="site">The <c>round.bomb.site</c> node: <c>"A"</c>, <c>"B"</c>, or <c>""</c>.</param>
/// <param name="plantPlace">The <c>round.bomb.plant_place</c> node: the raw place name.</param>
/// <param name="siteEntity">The <c>round.bomb.site_entity</c> node: <c>bomb_planted.Site</c>.</param>
/// <param name="readPlace">
///     Reads a slot's pre-frame place, or null when there is no entity scanner (the letter and place
///     then stay empty and only the entity index is written).
/// </param>
public sealed class BombPlantSiteEdge(
    StateNode source,
    GenericRoundScopedValueNode<string> site,
    GenericRoundScopedValueNode<string> plantPlace,
    GenericRoundScopedValueNode<int> siteEntity,
    Func<int, string?>? readPlace) : StateEdge(source)
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [plantPlace, siteEntity];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(BombPlantedEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => site;

    /// <summary>
    ///     The bombsite letter a nav-mesh place names: <c>"A"</c> for <c>BombsiteA</c>, <c>"B"</c> for
    ///     <c>BombsiteB</c> (any case), <c>""</c> for anything else. A map whose nav mesh names its
    ///     sites differently reads <c>""</c>, and the raw place is still in <c>plant_place</c>.
    /// </summary>
    /// <param name="place">The place name, possibly null.</param>
    /// <returns>The letter, or an empty string.</returns>
    public static string SiteLetter(string? place) =>
        place is { Length: 9 } && place.StartsWith("Bombsite", StringComparison.OrdinalIgnoreCase)
                               && char.ToUpperInvariant(place[8]) is 'A' or 'B'
            ? char.ToUpperInvariant(place[8]).ToString()
            : "";

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) =>
        context.Message is GameEventMessage { DecodedEvent.Payload: BombPlantedEvent planted } && Write(planted);

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context) =>
        payload is BombPlantedEvent planted && Write(planted);

    private bool Write(BombPlantedEvent planted)
    {
        string place = readPlace?.Invoke(planted.UserId) ?? "";
        site.SetValue(SiteLetter(place));
        plantPlace.SetValue(place);
        siteEntity.SetValue(planted.Site);
        return true;
    }
}
