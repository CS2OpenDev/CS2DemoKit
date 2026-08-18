namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Immutable snapshot of the entity-decode profiling accumulators, read once after a
///     replay completes. All tick fields are raw <c>Stopwatch</c> timestamps (convert with
///     <c>Stopwatch.GetElapsedTime</c> / <c>Stopwatch.Frequency</c>).
///     <para>
///         These fields are populated at runtime only when <see cref="Profiling.Enabled" /> was on
///         while this tracker decoded (set via <c>CS2DEMOKIT_PROFILE=1</c> or the <see cref="Profiling.Enabled" />
///         setter). Otherwise <see cref="EntityTracker.GetProfilingSnapshot" />
///         returns <c>default</c>, whose <see cref="Enabled" /> is <c>false</c> — the signal to callers
///         that no profiling data was captured.
///     </para>
///     <para>
///         The intervals are <b>nested</b>, not disjoint:
///         <see cref="PacketEntitiesTicks" /> brackets the whole <c>PacketEntities</c> decode,
///         which contains <see cref="FieldPathTicks" /> + <see cref="FieldValueTicks" /> +
///         <see cref="DescriptorBuildTicks" /> plus per-entity prelude overhead. Report them as
///         a tree with an explicit unattributed remainder.
///     </para>
/// </summary>
public readonly record struct EntityProfilingSnapshot(
    bool Enabled,
    long PacketEntitiesTicks,
    long FieldPathTicks,
    long FieldValueTicks,
    long DescriptorBuildTicks,
    long PacketEntitiesAlloc,
    long FieldPathAlloc,
    long FieldValueAlloc,
    long DescriptorBuildAlloc,
    int PacketEntitiesCount,
    int EntityFieldReads,
    int DescriptorBuilds);
