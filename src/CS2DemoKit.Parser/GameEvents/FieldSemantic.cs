namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     Describes the semantic meaning of a game-event integer field,
///     allowing the UI layer to enrich raw values with human-readable context.
/// </summary>
public enum FieldSemantic
{
    /// <summary>No special enrichment — display the raw value.</summary>
    None,

    /// <summary>
    ///     CS2 player controller entity index / game-event userid.
    ///     Can be resolved to a display name via <c>SlotToName(int)</c>.
    /// </summary>
    PlayerUserId,

    /// <summary>
    ///     CS2 entity handle (entity index + serial packed into one int).
    ///     Future enrichment: resolve via EntityTracker to entity class / player name.
    /// </summary>
    EntityHandle,

    /// <summary>
    ///     Raw entity index (not packed with serial).
    ///     Future enrichment: resolve via EntitySet[index].ClassName.
    /// </summary>
    EntityIndex
}
