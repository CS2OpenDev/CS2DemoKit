namespace CS2DemoKit.Parser;

/// <summary>
///     The single, process-wide runtime switch for ALL CS2DemoKit profiling instrumentation
///     (parse-pipeline, entity-decode, and the analysis evaluator's accumulator trees). Lives in the
///     Parser assembly — the lowest common layer every other project references — so EntityTracking,
///     Analysis and any host application all read the exact same flag.
///     <para>
///         <b>Default:</b> off. The flag resolves once from the <c>CS2DEMOKIT_PROFILE</c> environment
///         variable (<c>1</c>/<c>true</c>/<c>yes</c>, case-insensitive) the first time it is touched, so a
///         general user who never sets that env var pays nothing but a single predicted branch on each
///         instrumented seam's disabled path. Profiling is turned on either by that env var at process
///         start, or programmatically via the <see cref="Enabled" /> setter, which is the route to use when
///         a host wants to enable profiling for one run.
///     </para>
///     <para>
///         <b>Threading contract — set before the run.</b> <see cref="Enabled" /> is read on
///         <c>Parallel.For</c> worker threads (parse pass-2, the parallel digest producer). The flag must be
///         set <i>before</i> the run it governs begins; the <c>Parallel.For</c> fork is a full memory
///         barrier, so every worker observes the pre-fork value. A plain <see cref="bool" /> is sufficient
///         under this contract (≤ word-size reads are atomic per ECMA-335; no torn read). Hot-path call
///         sites that fan out additionally snapshot the flag into a local immediately before forking and
///         close over that local, so all workers in one run see one consistent value even if the contract
///         is ever violated mid-flight.
///     </para>
///     <para>
///         <b>Single profiled run at a time.</b> The accumulators this flag gates
///         (<c>ParseProfiler</c> statics, the per-tracker / per-scanner fields, the parallel producer's
///         per-worker alloc sum) assume one profiled run is in flight at a time, with the flag set before it
///         starts. The bench is single-shot per process; the App profiles via a deliberate reload / re-run.
///         Overlapping concurrent profiled runs would interleave the static accumulators — not a supported
///         configuration.
///     </para>
/// </summary>
public static class Profiling
{
    // Field initializer (NOT an explicit static constructor) so the type keeps `beforefieldinit`: the JIT
    // can elide the type-init check on the hot `Enabled` read instead of emitting one per access.

    /// <summary>
    ///     Whether profiling instrumentation is active. Reads are near-free (a static bool load); writes
    ///     should happen on the orchestrating thread before a run begins (see the threading contract on the
    ///     type). Set by <c>CS2DEMOKIT_PROFILE</c> at startup or through this setter.
    /// </summary>
    public static bool Enabled { get; set; } = ResolveEnvironment();

    private static bool ResolveEnvironment()
    {
        // DEMOVIEWER_PROFILE is the name this switch shipped under before the library moved out of
        // the DemoViewer.NET repo; it keeps working so existing scripts and docs do not silently
        // stop profiling.
        string? v = Environment.GetEnvironmentVariable("CS2DEMOKIT_PROFILE")
                    ?? Environment.GetEnvironmentVariable("DEMOVIEWER_PROFILE");
        return v == "1"
               || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
    }
}
