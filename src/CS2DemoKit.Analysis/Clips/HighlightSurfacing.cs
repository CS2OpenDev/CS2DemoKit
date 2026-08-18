#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Clips;

/// <summary>
///     The surfacing boundary between "every highlight that fired" and "the moments a reel or a
///     highlights page shows" (v0.5.4). Two rules, in this order:
///     <list type="number">
///         <item>
///             <b>Hidden drop.</b> <see cref="HighlightKind.Hidden" /> firings are counting-only —
///             their count feeds a rating stat (e.g. <c>kast</c> → <c>kast_pct</c>) and they are
///             never a moment. Their counts still flow through the normal stats path.
///         </item>
///         <item>
///             <b>Group supersession.</b> Within a <c>group:</c> family, a firing is dropped when a
///             HIGHER-scored firing of the same family exists for the same player+round, so a tiered
///             multikill (triple/quad/ace, all <c>group: multikill</c>) surfaces only its top tier.
///         </item>
///     </list>
///     Idempotent: surfacing an already-surfaced list returns the same list, so a consumer that
///     surfaces at store time and again at plan time is safe.
///     <para>
///         Tick clock: this layer never touches ticks. <c>HighlightFired.Tick</c> /
///         <c>ClipStartTick</c> stay exactly as emitted — the demo/frame clock, never
///         <c>− ParsedDemo.ServerStartTick</c>.
///     </para>
/// </summary>
public static class HighlightSurfacing
{
    /// <summary>
    ///     Applies the full surfacing policy: drops <see cref="HighlightKind.Hidden" /> firings, then
    ///     collapses each <c>group:</c> family to its top tier per player+round. Relative order of the
    ///     survivors is preserved.
    /// </summary>
    /// <param name="fired">Every highlight the run emitted (<c>AnalysisRun.Highlights</c>).</param>
    /// <returns>The firings worth showing, in input order.</returns>
    public static IReadOnlyList<HighlightFired> Surface(IReadOnlyList<HighlightFired> fired)
    {
        ArgumentNullException.ThrowIfNull(fired);

        List<HighlightFired> visible = [.. fired.Where(e => e.Kind != HighlightKind.Hidden)];
        return ApplyGroupSupersession(visible);
    }

    /// <summary>
    ///     Supersession only (<see cref="Surface" /> runs this after the Hidden drop): within a
    ///     <c>group:</c> family, drops a firing when a HIGHER-scored firing of the same family exists
    ///     for the same player+round. Strictly lower tiers only — same-score firings (e.g. two
    ///     distinct rapid doubles in one round) are separate moments and both survive. Ungrouped
    ///     firings (<c>Group == null</c>) always pass.
    /// </summary>
    /// <param name="surfaced">Firings that already passed the Hidden drop.</param>
    /// <returns>The surviving firings, in input order.</returns>
    public static IReadOnlyList<HighlightFired> ApplyGroupSupersession(IReadOnlyList<HighlightFired> surfaced)
    {
        ArgumentNullException.ThrowIfNull(surfaced);

        Dictionary<(int Slot, int Round, string Group), int> groupTopScore = surfaced
            .Where(e => e.Group is not null)
            .GroupBy(e => (e.PlayerSlot, e.RoundNumber, e.Group!))
            .ToDictionary(g => g.Key, g => g.Max(e => e.Score));

        return
        [
            .. surfaced.Where(e =>
                e.Group is null
                || !groupTopScore.TryGetValue((e.PlayerSlot, e.RoundNumber, e.Group), out int top)
                || e.Score >= top)
        ];
    }
}
