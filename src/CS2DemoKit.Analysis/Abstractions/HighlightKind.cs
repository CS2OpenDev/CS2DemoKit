namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     The editorial track a highlight firing belongs to, from the authored <c>kind:</c> key. Routes
///     a <see cref="HighlightFired" /> into the right reel: skill highlights, comedic moments, or
///     lowlights (notable failures). Defaults to <see cref="Highlight" /> when <c>kind:</c> is absent,
///     so <c>default(HighlightKind)</c> is the neutral case.
/// </summary>
public enum HighlightKind
{
    /// <summary>A normal skill highlight — the default when <c>kind:</c> is unspecified.</summary>
    Highlight,

    /// <summary>A funny / comedic moment (lucky blind kill, nade kill, jumping knife, …).</summary>
    Funny,

    /// <summary>A lowlight — a notable failure or embarrassing moment (teamkill, first death, …).</summary>
    Lowlight,

    /// <summary>
    ///     A counting-only firing that is NOT surfaced in any reel — it exists purely so its
    ///     match-scoped <c>.count</c> can feed a rating/stat (e.g. <c>kast</c> → <c>kast_pct</c>).
    ///     The DSL has no plain-stat way to count "rounds where a per-round flag held" (that idiom is
    ///     a highlight's <c>.count</c>), so a rating-input round flag is authored as a hidden highlight
    ///     rather than a watchable one. Surfacing layers (the reel harvester) drop these.
    /// </summary>
    Hidden
}
