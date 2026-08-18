namespace CS2DemoKit.Parser;

/// <summary>
///     Runtime opt-in switch for the entity-decode <b>bit-misalignment trace</b> — the
///     chronological per-op / per-field record the decoder keeps for one in-flight
///     <c>CSVCMsg_PacketEntities</c> packet so a decode failure can be root-caused down to the
///     offending field read. Lives in the Parser assembly (the lowest common layer) so
///     EntityTracking, Analysis and any host application all read the exact same flag.
///     <para>
///         <b>Independent of <see cref="Profiling" /> by design.</b> Profiling gates timing
///         accumulators on the hot path; tracing gates the per-op <c>DecodeTraceEntry</c>
///         construction + buffer append. They are deliberately separate switches: you want the
///         decode trace <i>without</i> paying for timing instrumentation, and the timing profile
///         <i>without</i> the ~7 M-entry-per-load trace overhead polluting the measurement. Either
///         can be on without the other.
///     </para>
///     <para>
///         <b>Default: off.</b> The flag resolves once from the <c>CS2DEMOKIT_TRACE_DECODE</c>
///         environment variable (<c>1</c>/<c>true</c>/<c>yes</c>, case-insensitive) the first time it
///         is touched, so a general run pays nothing but a single predicted branch on each gated
///         site's disabled path — and builds no trace entry. Turn it on via that env var at process
///         start, or programmatically via the <see cref="Enabled" /> setter (e.g. a deliberate
///         re-run to capture the full bit-trace of a decode error a default run only breadcrumbed).
///     </para>
///     <para>
///         <b>Threading contract — set before the run.</b> <see cref="Enabled" /> is read on
///         <c>Parallel.For</c> worker threads (the parallel digest producer). The flag must be set
///         <i>before</i> the run it governs begins; the <c>Parallel.For</c> fork is a full memory
///         barrier, so every worker observes the pre-fork value. A plain <see cref="bool" /> is
///         sufficient under this contract (≤ word-size reads are atomic per ECMA-335; no torn read).
///     </para>
/// </summary>
public static class Tracing
{
    // Field initializer (NOT an explicit static constructor) so the type keeps `beforefieldinit`:
    // the JIT can elide the type-init check on the hot `Enabled` read instead of emitting one per
    // access — mirrors the Profiling switch.

    /// <summary>
    ///     Whether the entity-decode trace is active. Reads are near-free (a static bool load);
    ///     writes should happen on the orchestrating thread before a run begins (see the threading
    ///     contract on the type). Set by <c>CS2DEMOKIT_TRACE_DECODE</c> at startup or programmatically.
    /// </summary>
    public static bool Enabled { get; set; } = ResolveEnvironment();

    private static bool ResolveEnvironment()
    {
        // DEMOVIEWER_TRACE_DECODE is the name this switch shipped under before the library moved
        // out of the DemoViewer.NET repo; it keeps working so existing scripts and docs do not
        // silently stop tracing.
        string? v = Environment.GetEnvironmentVariable("CS2DEMOKIT_TRACE_DECODE")
                    ?? Environment.GetEnvironmentVariable("DEMOVIEWER_TRACE_DECODE");
        return v == "1"
               || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
