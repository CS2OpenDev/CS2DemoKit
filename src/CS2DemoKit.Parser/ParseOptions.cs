namespace CS2DemoKit.Parser;

/// <summary>
///     Optional per-parse knobs for the <see cref="ParseOptions" /> overload of
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},ParseOptions,DemoProfile)" /> (0.8+). Every
///     property defaults to "off" — an empty <c>new ParseOptions()</c> makes the parse behave
///     exactly like the options-less overload.
///     <para>
///         A property bag (init-only properties), not a positional record: these are many
///         independent, mostly-unused knobs with more expected, and a positional record would force
///         every future addition to append to a growing constructor call.
///     </para>
/// </summary>
public sealed record ParseOptions
{
    /// <summary>
    ///     Cooperative cancellation. Checked at three points: before Pass 1, once per frame inside
    ///     the Pass 2 <c>Parallel.For</c> (also wired into <c>ParallelOptions.CancellationToken</c>
    ///     so the scheduler stops handing out new work), and before Pass 3. A canceled parse throws
    ///     <see cref="OperationCanceledException" /> — no partial <see cref="ParsedDemo" /> is ever
    ///     returned (mirrors <c>ParallelDigestProducer.Produce</c>'s identical contract).
    /// </summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>
    ///     Caps Pass 2's <c>Parallel.For</c> worker count. <c>null</c> or ≤0 means unbounded
    ///     (today's behavior). Set this on a multi-tenant host running several parses concurrently
    ///     so N parses don't each grab every core.
    /// </summary>
    public int? MaxDegreeOfParallelism { get; init; }

    /// <summary>
    ///     Fraction-complete callback (0.0–1.0) for Pass 2 ONLY. Pass 1 is near-zero cost and Pass 3
    ///     is a single sequential walk; Pass 2 dominates the parse. Throttled to ~200 calls total
    ///     regardless of frame count. Invoked from Pass 2 worker threads — implementations MUST be
    ///     thread-safe; the BCL's <see cref="Progress{T}" /> already satisfies this. Cross-thread
    ///     report ORDER is not guaranteed: two reports either side of a throttle boundary can reach
    ///     the marshaling target out of order, so treat the sequence as advancing-on-average rather
    ///     than strictly monotonic. The final report is always exactly <c>1.0</c>.
    /// </summary>
    public IProgress<double>? Progress { get; init; }

    /// <summary>
    ///     Per-parse callback for unknown net-message types. Fires IN ADDITION to the process-global
    ///     <see cref="DemoParser.OnUnknownMessageType" /> event, never instead of it. Use this
    ///     instead of the static event when parses may run concurrently — the event is shared by the
    ///     whole process, so a subscriber sees every concurrent parse's occurrences interleaved,
    ///     while this callback only ever sees its own parse's. Invoked from Pass 2 worker threads —
    ///     implementations MUST be thread-safe.
    /// </summary>
    public Action<UnknownMessageInfo>? OnUnknownMessage { get; init; }

    /// <summary>
    ///     When true, counts every net-message dropped during Pass 2 across all three known drop
    ///     sites — unknown types, known types whose protobuf decode failed, and a truncated
    ///     bitstream that abandons the rest of a frame's messages — keyed by resolved type name and
    ///     surfaced as <see cref="ParsedDemo.Warnings" /> entries
    ///     (<see cref="ParseWarningCodes.NetMessageDropped" />, with the tally in
    ///     <see cref="ParseWarning.Count" />) after Pass 3's own warnings have already claimed the
    ///     shared warning budget. A truncated bitstream is counted once per truncation EVENT under
    ///     the sentinel type name <c>&lt;bitstream-truncated&gt;</c> — the number of messages it
    ///     abandons is unknowable from where the truncation is detected.
    ///     <para>
    ///         Emission is capped to the top 8 distinct types by count plus one remainder summary:
    ///         an untrusted upload with a corrupted bitstream can otherwise synthesize hundreds of
    ///         distinct garbage type IDs and starve the structural-damage warnings out of the
    ///         budget.
    ///     </para>
    ///     <para>
    ///         Off by default: the underlying per-worker dictionary work only happens when true.
    ///     </para>
    /// </summary>
    public bool CountDropSites { get; init; }
}
