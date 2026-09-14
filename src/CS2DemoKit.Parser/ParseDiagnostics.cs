namespace CS2DemoKit.Parser;

/// <summary>
///     One structured parse warning — the S11 diagnostics channel. Travels as DATA on
///     <see cref="ParsedDemo.Warnings" />, never as an exception (the same house rule as
///     <c>RulesetDiagnostic</c>): a damaged demo still yields a usable partial parse, but the
///     damage is no longer silent, so the UI can say "this demo may be damaged" instead of
///     rendering a plausible-looking match with no players.
/// </summary>
/// <param name="Code">A stable code from <see cref="ParseWarningCodes" /> (machine-matchable).</param>
/// <param name="Message">Human-readable detail (table name, entry counts, …).</param>
/// <param name="Tick">The demo tick the warning arose at, when known.</param>
/// <param name="Count">
///     How many occurrences this one entry summarizes, when the warning is a tally rather than a
///     single event (<see cref="ParseWarningCodes.NetMessageDropped" />). <c>null</c> for the
///     one-warning-per-event codes. Machine-readable on purpose: the alternative — stuffing the
///     tally into <see cref="Message" /> — would force consumers to parse free text, which is
///     exactly what <see cref="ParseWarningCodes" /> exists to avoid. The dropped TYPE name still
///     lives in <see cref="Message" />: the set of net-message type names is protocol-version
///     dependent and unbounded, so no fixed code could enumerate it.
/// </param>
public sealed record ParseWarning(string Code, string Message, int? Tick = null, int? Count = null);

/// <summary>
///     The stable <see cref="ParseWarning.Code" /> catalogue. Add codes here (never inline
///     strings) so consumers can match without parsing message text.
/// </summary>
public static class ParseWarningCodes
{
    /// <summary>A <c>CreateStringTable</c> message failed to decode and was skipped.</summary>
    public const string StringTableCreateFailed = "string-table-create-failed";

    /// <summary>An <c>UpdateStringTable</c> message failed to decode and was skipped.</summary>
    public const string StringTableUpdateFailed = "string-table-update-failed";

    /// <summary>A full-snapshot string table exceeded the entry cap; the remainder was ignored.</summary>
    public const string StringTableTruncated = "string-table-truncated";

    /// <summary>A <c>userinfo</c> blob was present but unreadable — that player slot was dropped.</summary>
    public const string PlayerInfoUnreadable = "player-info-unreadable";

    /// <summary>
    ///     Net-messages were dropped during Pass 2 — an unknown type ID, a known type whose
    ///     protobuf decode failed, or a truncated bitstream that abandoned the rest of a frame.
    ///     Opt-in via <see cref="ParseOptions.CountDropSites" />; the dropped type name is in
    ///     <see cref="ParseWarning.Message" /> and the tally in <see cref="ParseWarning.Count" />.
    ///     Capped to the top 8 distinct types plus one remainder summary per parse.
    /// </summary>
    public const string NetMessageDropped = "net-message-dropped";

    /// <summary>
    ///     The per-parse warning cap was hit; this final entry reports how many further warnings
    ///     were suppressed. Always the LAST entry when present.
    /// </summary>
    public const string WarningsTruncated = "warnings-truncated";

    /// <summary>
    ///     The frame stream ended mid-header or mid-payload: the recording was cut short. Frames up
    ///     to that point are intact and returned.
    /// </summary>
    public const string DemoTruncated = "demo-truncated";

    /// <summary>
    ///     A frame header declared a size that cannot be real. Frame offsets chain, so the scan
    ///     cannot resynchronize past it and stops there; frames before it are intact and returned.
    /// </summary>
    public const string FrameStreamCorrupt = "frame-stream-corrupt";

    /// <summary>
    ///     How much a given code says about the DEMO, as opposed to about this parser. See
    ///     <see cref="ParsedDemo.Health" />, which folds these into one signal.
    /// </summary>
    /// <remarks>
    ///     The split that matters is <see cref="ParseWarningCodes.NetMessageDropped" />: a dropped
    ///     net message usually means Valve shipped a message type this parser has no case for yet,
    ///     which is this library being behind rather than the demo being damaged. Grading it with
    ///     structural decode failures would make every demo from a new build look broken.
    /// </remarks>
    public static ParseHealth SeverityOf(string code) => code switch
    {
        StringTableCreateFailed => ParseHealth.Damaged,
        StringTableUpdateFailed => ParseHealth.Damaged,
        StringTableTruncated => ParseHealth.Damaged,
        PlayerInfoUnreadable => ParseHealth.Damaged,
        DemoTruncated => ParseHealth.Damaged,
        FrameStreamCorrupt => ParseHealth.Damaged,

        // The parser is behind, or diagnostics themselves were lost. Neither indicts the demo.
        NetMessageDropped => ParseHealth.Degraded,
        WarningsTruncated => ParseHealth.Degraded,

        // An unrecognized code is a code added without a severity. Grade it as damage so the
        // omission shows up as a too-pessimistic reading rather than a silently clean one.
        _ => ParseHealth.Damaged
    };
}

/// <summary>
///     How much to trust a <see cref="ParsedDemo" />. Ordered, so <c>&gt;</c> comparisons work and
///     <see cref="ParsedDemo.Health" /> can take the max across warnings.
/// </summary>
public enum ParseHealth
{
    /// <summary>No warnings. Every byte the parser looked at decoded.</summary>
    Clean = 0,

    /// <summary>
    ///     The demo parsed, but something was lost on THIS side: an unparsed message type, or
    ///     diagnostics past the cap. The demo itself is not implicated, and the parsed output is
    ///     normally still usable as-is.
    /// </summary>
    Degraded = 1,

    /// <summary>
    ///     Part of the demo's own data did not decode, or the recording is incomplete. Output is a
    ///     best-effort partial: present values are trustworthy, but absences may be damage rather
    ///     than fact. Surface this to a user rather than rendering it as a clean match.
    /// </summary>
    Damaged = 2
}

/// <summary>
///     One parse's or one read's warning accumulator. Owned by the decode that created it and
///     handed to every site that can warn, so a result carries exactly the warnings its own run
///     produced and a forward reader can expose them while the read is still going.
///     Single-threaded: the scan, the enrichment and the string-table decode all run on the
///     consuming thread. Pass-2 workers never warn; their drop tallies are merged after the join.
/// </summary>
internal sealed class ParseDiagnostics
{
    // Soft cap: a demo whose EVERY table is damaged must not accumulate an unbounded list. Past
    // the cap the count still advances via a final summary warning.
    public const int MaxWarnings = 256;

    private readonly List<ParseWarning> _warnings = [];
    private int _dropped;

    /// <summary>Warnings recorded so far, not counting any suppressed past the cap.</summary>
    public int Count => _warnings.Count;

    /// <summary>
    ///     Records one warning (cheap; cap-bounded). <paramref name="count" /> is the occurrence
    ///     tally for summary-shaped warnings, see <see cref="ParseWarning.Count" />; leave it null
    ///     for one-warning-per-event codes.
    /// </summary>
    public void Warn(string code, string message, int? tick = null, int? count = null)
    {
        if (_warnings.Count >= MaxWarnings)
        {
            _dropped++;
            return;
        }

        _warnings.Add(new ParseWarning(code, message, tick, count));
    }

    /// <summary>
    ///     A copy of the warnings so far, in the shape <see cref="Drain" /> would return, without
    ///     clearing anything. The live view a reader exposes mid-stream.
    /// </summary>
    public IReadOnlyList<ParseWarning> Snapshot()
    {
        if (_warnings.Count == 0)
        {
            return [];
        }

        List<ParseWarning> copy = new(_warnings.Count + 1);
        copy.AddRange(_warnings);
        AppendTruncation(copy, _dropped);
        return copy;
    }

    /// <summary>Returns the warnings and resets, so the result owns them.</summary>
    public IReadOnlyList<ParseWarning> Drain()
    {
        if (_warnings.Count == 0)
        {
            _dropped = 0;
            return [];
        }

        List<ParseWarning> list = new(_warnings.Count + 1);
        list.AddRange(_warnings);
        AppendTruncation(list, _dropped);
        _warnings.Clear();
        _dropped = 0;
        return list;
    }

    private static void AppendTruncation(List<ParseWarning> list, int dropped)
    {
        if (dropped > 0)
        {
            list.Add(new ParseWarning(ParseWarningCodes.WarningsTruncated, $"{dropped} further warning(s) suppressed."));
        }
    }
}
