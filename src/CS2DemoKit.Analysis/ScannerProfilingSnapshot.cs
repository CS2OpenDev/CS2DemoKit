namespace CS2DemoKit.Analysis;

/// <summary>
///     Immutable snapshot of <see cref="EntityChangeScanner" />'s per-frame profiling
///     accumulators. All tick fields are raw <c>Stopwatch</c> timestamps.
///     <para>
///         Populated at runtime only when <see cref="CS2DemoKit.Parser.Profiling.Enabled" /> was on
///         while this scanner ran (set via <c>CS2DEMOKIT_PROFILE=1</c> or the
///         <see cref="CS2DemoKit.Parser.Profiling.Enabled" /> setter). Otherwise <see cref="EntityChangeScanner.GetProfilingSnapshot" />
///         returns <c>default</c> and <see cref="Enabled" /> is <c>false</c>.
///     </para>
///     <para>
///         <see cref="SeekTicks" /> is the outer cost of advancing the entity layer one frame;
///         it transitively contains the EntityTracker-internal decode reported separately by
///         <c>EntityTracker.GetProfilingSnapshot()</c>. The other three are sibling per-frame
///         sub-phases of <see cref="EntityChangeScanner.AdvanceAndPollAt" />.
///     </para>
///     <para>
///         Only the sequential producer drives those. Under the pipelined producer the fold runs
///         on worker threads and the per-frame <see cref="SeekTicks" />/<see cref="SnapshotTicks" />
///         stay near zero; the fold's cost lands in <see cref="FoldTicks" /> and
///         <see cref="FoldAlloc" /> instead: the digest producer's worker time and allocation,
///         summed over its workers, whether the fold ran under the evaluation or up front in
///         <see cref="EntityChangeScanner.PrecomputeParallelDigests" />. Worker time, not wall:
///         with three workers it can read three times the wall the fold took, and it overlaps the
///         evaluation's own time rather than adding to it. <see cref="PrecomputeTicks" /> and
///         <see cref="PrecomputeAlloc" /> are the same two numbers under the name a host read
///         before the fold moved onto the evaluation. <see cref="ProviderPollTicks" />/<see cref="ProjectileScanTicks" />
///         are legacy sub-phases folded into the snapshot/digest build since the Track-4 seam and
///         always zero.
///     </para>
/// </summary>
public readonly record struct ScannerProfilingSnapshot(
    bool Enabled,
    long SeekTicks,
    long ProviderPollTicks,
    long ProjectileScanTicks,
    long SnapshotTicks,
    long SeekAlloc,
    long ProviderPollAlloc,
    long ProjectileScanAlloc,
    long SnapshotAlloc,
    int FramesPolled,
    long FoldTicks = 0,
    long FoldAlloc = 0)
{
    /// <summary><see cref="FoldTicks" /> under its earlier name: the producer's fold cost on either path, not only the up-front one.</summary>
    public long PrecomputeTicks => FoldTicks;

    /// <summary><see cref="FoldAlloc" /> under its earlier name.</summary>
    public long PrecomputeAlloc => FoldAlloc;
}
