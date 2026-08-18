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
///         sub-phases of <see cref="EntityChangeScanner.AdvanceAndPoll" />.
///     </para>
///     <para>
///         <see cref="PrecomputeTicks" /> is the Track-4 Step-3 up-front parallel decode
///         (<c>PrecomputeParallelDigests</c>). When digests are precomputed the per-frame
///         <see cref="SeekTicks" />/<see cref="SnapshotTicks" /> stay near zero and the decode cost lands
///         here instead; <see cref="ProviderPollTicks" />/<see cref="ProjectileScanTicks" /> are legacy
///         sub-phases folded into the snapshot/digest build since the Track-4 seam and now always zero.
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
    long PrecomputeTicks = 0,
    long PrecomputeAlloc = 0);
