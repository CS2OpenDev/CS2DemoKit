namespace CS2DemoKit.Analysis.RulesetsV2.Model;

/// <summary>
///     One <c>cadences:</c> entry: a period on the frame clock, anchored on a round boundary, that a
///     stat triggers on as <c>cadence.&lt;id&gt;</c>. It fires once on the first frame at or after
///     each due tick, so a per-player sample costs one dispatch per period, not one per frame.
///     A step dispatches after that frame's per-pawn fold, so its <c>player.*</c> reads are the
///     frame's own state, as a frame observer reads it, not the previous frame's.
/// </summary>
/// <param name="Id">The cadence id (the map key), referenced as <c>cadence.&lt;id&gt;</c>.</param>
/// <param name="Ticks">
///     The period in ticks (<c>ticks:</c>). On a resolved ruleset this is always set: a
///     <c>seconds:</c> period is folded into it at the demo's tick rate.
/// </param>
/// <param name="Seconds">The period in seconds (<c>seconds:</c>); <c>null</c> once resolved.</param>
/// <param name="Anchor">The boundary that starts the schedule at step 0 (<c>anchor:</c>).</param>
/// <param name="Until">The boundary that stops it (<c>until:</c>); <see cref="CadenceStop.None" /> when absent.</param>
/// <param name="Position">The document-absolute position of the entry.</param>
public sealed record CadenceDef(
    string Id,
    int? Ticks,
    double? Seconds,
    CadenceAnchor Anchor,
    CadenceStop Until,
    SourcePosition Position)
{
    /// <summary>
    ///     The build-wide identity of a resolved cadence, <c>&lt;id&gt;:&lt;ticks&gt;:&lt;anchor&gt;:&lt;until&gt;</c>.
    ///     It is the value <c>event.Cadence</c> carries, so two rulesets that reuse an id with different
    ///     periods never fire each other's stats, and the period enters the stat hash.
    /// </summary>
    public string Key =>
        $"{Id}:{Ticks?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}:{AnchorName(Anchor)}:{StopName(Until)}";

    /// <summary>The YAML spelling of an anchor.</summary>
    /// <param name="anchor">The anchor.</param>
    /// <returns>The spelling, <c>none</c> for <see cref="CadenceAnchor.None" />.</returns>
    public static string AnchorName(CadenceAnchor anchor) =>
        anchor switch
        {
            CadenceAnchor.RoundFreezeEnd => "round_freeze_end",
            _ => "none"
        };

    /// <summary>The YAML spelling of a stop.</summary>
    /// <param name="stop">The stop.</param>
    /// <returns>The spelling, <c>none</c> for <see cref="CadenceStop.None" />.</returns>
    public static string StopName(CadenceStop stop) =>
        stop switch
        {
            CadenceStop.RoundEnded => "round_ended",
            CadenceStop.NextFreezeEnd => "next_freeze_end",
            _ => "none"
        };
}

/// <summary>The boundary a cadence starts on (<c>anchor:</c>).</summary>
public enum CadenceAnchor
{
    /// <summary>Unset. A cadence without an anchor is a structural error.</summary>
    None = 0,

    /// <summary>The profile's <c>$round_freeze_end</c>, plus the freeze end the evaluator synthesizes.</summary>
    RoundFreezeEnd
}

/// <summary>The boundary a cadence stops on (<c>until:</c>).</summary>
public enum CadenceStop
{
    /// <summary>No stop: the schedule runs until the next anchor restarts it.</summary>
    None = 0,

    /// <summary>The profile's <c>$round_end</c>, the round's close.</summary>
    RoundEnded,

    /// <summary>The next freeze end, which also restarts the schedule.</summary>
    NextFreezeEnd
}
