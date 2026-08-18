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
}

/// <summary>
///     The per-parse warning accumulator (S11). Modeled on <c>ParseProfiler</c>: an internal
///     static living in an UNPROTECTED file, so instrumented sites (string tables today; more
///     later) can report without threading state through the protected parse pipeline.
///     <para>
///         <b>Isolation is thread-affine.</b> The store is <see cref="ThreadStaticAttribute" />:
///         a parse runs pass 3 and constructs its <see cref="ParsedDemo" /> on one thread, and
///         <see cref="Drain" /> (called from the <see cref="ParsedDemo" /> ctor) empties that
///         thread's list — so drain-on-construct doubles as the per-parse reset, and concurrent
///         parses on the background queue cannot cross-contaminate. A parse that THROWS before
///         constructing its result leaves residue on its thread; the next successful parse on
///         that thread drains it away, which at worst attributes a dead parse's warnings to its
///         successor — accepted, because warnings are advisory and the alternative is a reset
///         hook inside the protected <c>DemoParser.cs</c>.
///     </para>
/// </summary>
internal static class ParseDiagnostics
{
    // Soft cap: a demo whose EVERY table is damaged must not accumulate an unbounded list (the
    // repo's no-unbounded-diagnostics invariant). Past the cap the count still advances via a
    // final summary warning.
    private const int MaxWarnings = 256;

    [ThreadStatic]
    private static List<ParseWarning>? _warnings;

    [ThreadStatic]
    private static int _dropped;

    /// <summary>
    ///     Records one warning on the current parse thread (cheap; cap-bounded).
    ///     <paramref name="count" /> is the occurrence tally for summary-shaped warnings — see
    ///     <see cref="ParseWarning.Count" />; leave it null for one-warning-per-event codes.
    /// </summary>
    public static void Warn(string code, string message, int? tick = null, int? count = null)
    {
        List<ParseWarning> list = _warnings ??= [];
        if (list.Count >= MaxWarnings)
        {
            _dropped++;
            return;
        }

        list.Add(new ParseWarning(code, message, tick, count));
    }

    /// <summary>
    ///     Returns and clears the current thread's warnings — called by the
    ///     <see cref="ParsedDemo" /> constructor, so every parse result carries exactly the
    ///     warnings its own run produced.
    /// </summary>
    public static IReadOnlyList<ParseWarning> Drain()
    {
        List<ParseWarning>? list = _warnings;
        int dropped = _dropped;
        _warnings = null;
        _dropped = 0;
        if (list is null || list.Count == 0)
        {
            return [];
        }

        if (dropped > 0)
        {
            list.Add(new ParseWarning(ParseWarningCodes.WarningsTruncated, $"{dropped} further warning(s) suppressed."));
        }

        return list;
    }
}
