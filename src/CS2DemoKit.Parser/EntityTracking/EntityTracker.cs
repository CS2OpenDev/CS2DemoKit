#region

using System.Collections;
using System.Diagnostics;
using System.Globalization;
using CS2DemoKit.Parser.Entities;
using Snappier;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Stateful processor that replays a list of <see cref="DemoFrame" /> objects and
///     reconstructs the entity state for every tick by decoding <c>svc_PacketEntities</c>
///     entity_data bit streams.
///     <para>
///         This is an opt-in layer on top of <see cref="DemoParser" />.
///         The DemoParser API is unchanged — EntityTracker accepts its output.
///     </para>
///     <para>Adapted from demofile-net's DemoParser.Entities.cs and related infrastructure (MIT).</para>
/// </summary>
public sealed class EntityTracker
{
    /// <summary>
    ///     Pre-generated element descriptor count for variable-length arrays. CCSPlayerPawn's
    ///     animation graph state bag contains element indices into the high hundreds (538 observed
    ///     on furia-vs-vitality m1 mirage). 1024 covers all observed CS2 cases with negligible
    ///     memory cost (~50KB per nested array per class).
    /// </summary>
    private const int ArrayPregenSize = 1024;
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int MaxEdicts = 1 << 14; // 16 384
    private const int NumSerialNumberBits = 17;

    // Serializes the first-error report (breadcrumb + optional full DumpTrace) across parallel
    // decode workers. Each worker is a separate EntityTracker instance, so _errorLogged
    // is per-tracker ("first error" is per-worker); without this lock their multi-line reports
    // interleave into unreadable, shredded output. Static so it spans all workers in a parallel
    // decode — and it still applies once DecodeDiagnosticSink is redirected, since a collector
    // shared by several workers has exactly the same interleaving problem the console did.
    private static readonly object _decodeErrorConsoleLock = new();

    // ── Class registry ────────────────────────────────────────────────────────

    // classId (int) → serializer name
    private readonly Dictionary<int, string> _classIdToName = new();

    // ── Per-class Schema Lens shape ──────────────────────────────────────────
    //
    // (serializerName) → ClassShape. Built side-by-side with the descriptor list
    // by BuildFieldDescs the first time we walk a serializer. Bound to every
    // EntityState that flows through ReadEntityFields so lane-indexed writes
    // can happen in O(1) without consulting any name lookup on the hot path.
    private readonly Dictionary<string, ClassShape> _classShapes = new();

    // ── Entity wrapper factory registry ──────────────────────────────────────
    //
    // (serializerName) → Func<EntityState, EntityTracker, object> that constructs
    // the typed wrapper for entities of that class. Populated externally — since the
    // cutover, by TrackerEntityWorld.RegisterWrapper installing SDK-wrapper factories;
    // EntityTracker.Get<T> / Snapshot<T> / ResolveHandle<T> dispatch through this map.
    private readonly Dictionary<string, Func<EntityState, EntityTracker, object>> _entityFactories = new();

    // ── Decoded field descriptors ─────────────────────────────────────────────

    // (serializerName) → flat list of (dotted path string, FieldDecoder)
    // Built lazily on first entity create for that class.
    private readonly Dictionary<string, List<FieldDescriptor>> _fieldDescs = new();

    // Reusable field-path scratch for the hot Replay decode path (ReadEntityFields). Single-threaded
    // per tracker and only used by ProcessPacketEntitiesCore — PeekEntityUpdates passes its own list,
    // mirroring the _trace isolation above, so a UI peek can never disturb Replay's buffer.
    private readonly List<FieldPath> _fieldPathScratch = new(32);

    // instancebaseline string-table reconstruction state. The table is created once
    // (CSVCMsg_CreateStringTable name="instancebaseline") then mutated by
    // CSVCMsg_UpdateStringTable. Entries persist across updates (an update may omit the
    // key, meaning "edit the existing entry at this index") and substring-compressed keys
    // reference the running key history — so we keep the full entries list, not just the
    // classId map. Faithful port of demofile-net's StringTable.ReadUpdate. A fresh tracker
    // (the only replay model — SeekToTick is forward-only, Reset makes a new tracker) starts
    // these at defaults, so no explicit reset hook is needed.
    private readonly List<KeyValuePair<string, byte[]>> _ibEntries = new();

    // ── Instance baselines ────────────────────────────────────────────────────

    // classId → byte[] baseline snapshot
    private readonly Dictionary<int, byte[]> _instanceBaselines = new();

    // ── Decode trace ──────────────────────────────────────────────────────────
    //
    // Captures every path-op + field-read inside one PacketEntities message so we can
    // identify which earlier field's bitsConsumed didn't match its declared metadata
    // (the perpetrator of the cascade that eventually crashes a later entity's
    // FieldPath decode). Bounded only by per-packet decode cost; cleared on each
    // packet to keep memory flat. Dumped on first LastEntityError to ease post-mortem.
    //
    // Only ProcessPacketEntitiesCore sets/clears trace context — PeekEntityUpdates
    // does NOT, so concurrent UI peeks don't pollute the buffer used by Replay.

    private readonly List<DecodeTraceEntry> _trace = new(4096);
    private int _curEntityIndex;
    private string _curUpdateKind = "";

    private bool _errorLogged;
    private bool _wrapperBootstrapWarned;
    private int _ibFlags;
    private bool _ibInitialized;
    private int _ibTableId = -1;
    private bool _ibUserDataFixedSize;
    private int _ibUserDataSizeBits;
    private bool _ibUsingVarintBitcounts;

    // ── Schema Lens resolver ─────────────────────────────────────────────────
    //
    // Optional injection hook. When bound, BuildFieldDescs consults the resolver
    // at every leaf descriptor on the non-array spine and uses the returned
    // LensSlotRule to pick the lane / transform / fallback default. When null,
    // BuildFieldDescs falls through to plain DecoderKind-based classification
    // with no transforms or defaults.
    //
    // EntityTracking sits below the Entities folder in the dependency graph
    // (Entities-side code builds ON EntityTracking types like EntityState),
    // so EntityTracker cannot reach for GeneratedLensRegistry / LensState
    // directly — the caller (anywhere that sees both projects: Entities.Tests,
    // a future analysis bootstrap, etc.) supplies the resolver.
    private LensResolver? _lensResolver;
    private long _profDescriptorBuildAlloc;
    private int _profDescriptorBuilds;
    private long _profDescriptorBuildTicks;
    private int _profEntityFieldReads;
    private long _profFieldPathAlloc;
    private long _profFieldPathTicks;
    private long _profFieldValueAlloc;

    private long _profFieldValueTicks;

    // Whether this tracker captured profiling data — latched the first time a profiled decode runs through
    // a gated seam. Reported as EntityProfilingSnapshot.Enabled, decoupled from the live flag so a snapshot
    // read after the flag toggles reflects what was actually captured.
    private bool _profiled;

    // Allocated-bytes deltas for the same sub-phases (GC.GetAllocatedBytesForCurrentThread is a
    // non-allocating intrinsic, so bracketing it does not perturb the measurement). Attributes the
    // eval-level allocation total to the decode sub-phase that produces it.
    private long _profPacketEntitiesAlloc;
    private int _profPacketEntitiesCount;

    // ── Entity-decode profiling (opt-in at RUNTIME via Profiling.Enabled) ──────
    //
    // Stopwatch-tick accumulators for the entity-decode hot path. The call-sites that fold into them are
    // guarded by `if (Profiling.Enabled)`, so a default replay touches none of them (a single predicted
    // branch per seam). Bracketing is per-entity / per-packet (not per-field), keeping GetTimestamp
    // overhead a small fraction of the measured interval. Read once post-run via GetProfilingSnapshot().
    private long _profPacketEntitiesTicks;

    // ── Schema ────────────────────────────────────────────────────────────────

    // classId → log2(classCount) bits needed to read class IDs
    private int _serverClassBits;
    private int _stringTableCreateCount;

    // ── Entity field reading ──────────────────────────────────────────────────

    // Opt-in field-storage allowlist (score-replay optimization). When set, entities whose class is
    // NOT in the set are still fully DECODED (the bitstream is sequential — their bits must be consumed
    // to reach the next entity) but their field VALUES are not stored (no lane bind, no Set*). Entity
    // existence + class are unaffected, so a consumer that only reads one class (e.g. Library reading
    // CCSTeam.m_iScore) gets a byte-identical result while skipping the storage/allocation for every
    // other class. Null (default) stores everything — byte-identical to every existing consumer.
    private bool _suppressFieldStore;
    private bool _traceContextActive;

    /// <summary>
    ///     Test-only window onto the decode-trace buffer's current size. Lets the trace-gate test prove
    ///     the gate's defining property deterministically on a healthy demo: after a full Replay the
    ///     buffer holds the last PacketEntities packet's entries when <see cref="Tracing.Enabled" />
    ///     is on, and is empty when it is off (the cleared-per-packet semantics keep the buffer to
    ///     one packet). Internal, surfaced to the test assemblies via InternalsVisibleTo — mirrors
    ///     the <see cref="DebugDescriptors" /> debug-accessor precedent.
    /// </summary>
    internal int TraceEntryCountForTest => _trace.Count;

    // ── Class-browser surface ─────────────────────────────────────────────────

    /// <summary>
    ///     All known entity class names (serializer names) registered from DEM_ClassInfo /
    ///     svc_ClassInfo. Lets the UI render the class-browser left rail without re-parsing
    ///     send-tables. Order follows the registry's insertion order; callers that want a
    ///     stable display order should sort. Empty until a ClassInfo frame has been processed.
    /// </summary>
    public IEnumerable<string> AvailableClasses => _classIdToName.Values;

    /// <summary>
    ///     Read-only view of the classId → class-name registry. Backs the class-browser's
    ///     id column and the command-palette "find class" source. Live view —
    ///     reflects later ClassInfo updates; read on the UI thread after a seek completes.
    /// </summary>
    public IReadOnlyDictionary<int, string> ClassIdMap => _classIdToName;

    // ── Live entity state ─────────────────────────────────────────────────────

    /// <summary>Current entities.</summary>
    public EntitySet CurrentEntities { get; } = new();

    /// <summary>
    ///     Zero-based index of the last frame processed by this tracker.
    ///     Matches <see cref="DemoFrame.FrameNumber" /> and is always monotonically
    ///     increasing regardless of the frame's <see cref="DemoFrame.ServerTick" />
    ///     (which can be non-monotonic due to DEM_FullPacket checkpoint frames).
    ///     -1 when no frame has been processed yet.
    /// </summary>
    public int CurrentFrameIndex { get; private set; } = -1;

    /// <summary>Current tick.</summary>
    public int CurrentTick { get; private set; }

    /// <summary>DIAGNOSTIC: count of delta-on-unknown-entity events across the whole replay.</summary>
    public int DeltaUnknownCount { get; private set; }

    /// <summary>Most-recent entity decode error (null if none). Exposed for diagnostics.</summary>
    public string? LastEntityError { get; private set; }

    /// <summary>
    ///     1-based index of the most recently processed CSVCMsg_PacketEntities. Used by the
    ///     UI debugger to set Tier 3 "break on packet #N" breakpoints, and to surface
    ///     "Tracker: 37 packets" status without reaching into private state.
    /// </summary>
    public int PacketCount { get; private set; }

    /// <summary>The deserialized FlattenedSerializer schema. Available after a DEM_SendTables frame.</summary>
    public RuntimeSchema? Schema { get; private set; }

    /// <summary>
    ///     When non-null, only entities whose <c>ClassName</c> is in this set have their decoded field
    ///     values stored; all other classes are decoded-and-discarded (bits still consumed, so the score
    ///     for the stored classes is unchanged). Null = store every class (default). Set before a replay
    ///     that reads only specific classes to skip the per-field storage cost for the rest.
    /// </summary>
    public IReadOnlySet<string>? StoreClassFilter { get; set; }

    /// <summary>
    ///     Returns the entity-decode profiling accumulators captured so far. Returns <c>default</c>
    ///     (<see cref="EntityProfilingSnapshot.Enabled" /> is <c>false</c>) when no profiled decode has run
    ///     on this tracker — see <see cref="Profiling.Enabled" />. See <see cref="EntityProfilingSnapshot" />
    ///     for the nesting contract.
    /// </summary>
    public EntityProfilingSnapshot GetProfilingSnapshot() =>
        _profiled
            ? new EntityProfilingSnapshot(true, _profPacketEntitiesTicks, _profFieldPathTicks, _profFieldValueTicks,
                _profDescriptorBuildTicks, _profPacketEntitiesAlloc, _profFieldPathAlloc,
                _profFieldValueAlloc, _profDescriptorBuildAlloc, _profPacketEntitiesCount,
                _profEntityFieldReads, _profDescriptorBuilds)
            : default;

    /// <summary>
    ///     Replays up to (and including) <paramref name="targetTick" />.
    ///     <para>
    ///         <b>This always starts from frame 0.</b> It is not a seek from the tracker's current
    ///         position: calling it repeatedly with increasing ticks re-replays everything already
    ///         processed each time, which is O(n²) over the walk and duplicates every side effect
    ///         (<see cref="EntityCreated" />, <see cref="PacketProcessed" />, the decode counters).
    ///         Nothing throws or warns — the result is simply slow. For a forward walk over many
    ///         ticks use <c>EntityStateLayer</c> in CS2DemoKit.Analysis, which keeps its position
    ///         and advances only the frames in between; or step frames yourself with
    ///         <see cref="AdvanceOneFrame" />.
    ///     </para>
    /// </summary>
    public void AdvanceTo(int targetTick, IReadOnlyList<DemoFrame> frames)
    {
        foreach (DemoFrame frame in frames)
        {
            if (frame.ServerTick > targetTick)
            {
                break;
            }

            ProcessFrame(frame);
        }
    }

    /// <summary>
    ///     Replays up to and including <paramref name="frameIndex" /> (0-based position in the list).
    ///     Prefer this over <see cref="AdvanceTo" /> when you want frame-accurate seeking, since
    ///     multiple frames can share the same tick value.
    ///     <para>
    ///         <b>This always starts from frame 0</b>, regardless of where the tracker already is —
    ///         it is a replay-from-scratch, not an incremental seek. That is the right shape for a
    ///         one-off jump onto a fresh tracker (which is how <see cref="EntitySeekService" /> uses
    ///         it), and the wrong shape for a loop: N increasing indices cost O(N²) frames and
    ///         re-fire every side effect (<see cref="EntityCreated" />,
    ///         <see cref="PacketProcessed" />, the decode counters) for the frames replayed again.
    ///         Nothing throws or warns. For a forward walk use <c>EntityStateLayer</c> in
    ///         CS2DemoKit.Analysis, which advances only the frames between its position and the
    ///         target; or step frames yourself with <see cref="AdvanceOneFrame" />.
    ///     </para>
    /// </summary>
    public void AdvanceToIndex(int frameIndex, IReadOnlyList<DemoFrame> frames)
    {
        int limit = Math.Min(frameIndex + 1, frames.Count);
        for (int i = 0; i < limit; i++)
        {
            ProcessFrame(frames[i]);
        }
    }

    /// <summary>
    ///     Advances the tracker by exactly one frame from its current position, mutating
    ///     <see cref="CurrentEntities" /> / <see cref="CurrentTick" /> / <see cref="CurrentFrameIndex" />
    ///     in place. The caller guarantees <paramref name="frame" /> is the immediate successor of the
    ///     last processed frame. Enables real-time playback without the O(N)-from-zero cost of
    ///     <see cref="AdvanceToIndex" />: one <c>AdvanceOneFrame</c> per playback tick is O(1).
    ///     <para>
    ///         Behaviour-identical to a single iteration of <see cref="AdvanceToIndex" />'s loop body
    ///         (both call the same private per-frame primitive); a fresh tracker stepped
    ///         <c>AdvanceOneFrame</c> N times yields the same <see cref="EntitySet" /> as
    ///         <c>AdvanceToIndex(N − 1, frames)</c>.
    ///     </para>
    /// </summary>
    public void AdvanceOneFrame(DemoFrame frame) => ProcessFrame(frame);

    /// <summary>
    ///     Advances to <paramref name="snapshotAt" />, takes a snapshot, then continues to
    ///     <paramref name="frameIndex" />. Returns the snapshot taken at <paramref name="snapshotAt" />.
    /// </summary>
    public Dictionary<int, Dictionary<string, object?>> AdvanceToIndexWithSnapshot(
        int snapshotAt, int frameIndex, IReadOnlyList<DemoFrame> frames)
    {
        int snapLimit = Math.Min(snapshotAt + 1, frames.Count);
        for (int i = 0; i < snapLimit; i++)
        {
            ProcessFrame(frames[i]);
        }

        Dictionary<int, Dictionary<string, object?>> snapshot = SnapshotCurrentFields();

        int limit = Math.Min(frameIndex + 1, frames.Count);
        for (int i = snapLimit; i < limit; i++)
        {
            ProcessFrame(frames[i]);
        }

        return snapshot;
    }

    /// <summary>
    ///     Debug-only: returns the built descriptor list for <paramref name="className" /> as
    ///     (path, typeName, encoder, bitCount, encodeFlags, childCount) tuples for inspection.
    ///     Used by EntityDecodeProbe to compare descriptor state against schema state and find
    ///     off-by-one bit-misalignment mismatches. Pass <paramref name="indexPath" /> to traverse
    ///     into nested child descriptors (e.g. [9] for CBodyComponent under CCSPlayerPawn).
    /// </summary>
    public IReadOnlyList<(string Path, string? TypeName, string? Encoder, int BitCount, int EncodeFlags, int ChildCount)> DebugDescriptors(string className, params int[] indexPath)
    {
        List<FieldDescriptor>? descs = GetFieldDescriptors(className);
        if (descs is null)
        {
            return Array.Empty<(string, string?, string?, int, int, int)>();
        }

        IReadOnlyList<FieldDescriptor>? cur = descs;
        foreach (int i in indexPath)
        {
            if (cur is null || i < 0 || i >= cur.Count)
            {
                return Array.Empty<(string, string?, string?, int, int, int)>();
            }

            cur = cur[i].ChildDescs;
        }

        if (cur is null)
        {
            return Array.Empty<(string, string?, string?, int, int, int)>();
        }

        List<(string, string?, string?, int, int, int)> result = new(cur.Count);
        foreach (FieldDescriptor d in cur)
        {
            result.Add((d.Path, d.Field?.TypeName, d.Field?.Encoder, d.Field?.BitCount ?? 0, d.Field?.EncodeFlags ?? 0, d.ChildDescs?.Count ?? 0));
        }

        return result;
    }

    /// <summary>
    ///     Raised once per packet-level decode failure (the same point that sets
    ///     <see cref="LastEntityError" />). The app's Output panel subscribes at file-load to
    ///     populate its "Decode errors" channel. Fires only from the
    ///     mutating Replay path (<see cref="ProcessPacketEntities" />), never from
    ///     <see cref="PeekEntityUpdates" />.
    /// </summary>
    public event Action<DecodeError>? DecodeErrorRaised;

    /// <summary>
    ///     Where this tracker writes its human-readable decode diagnostics: the first-decode-error
    ///     breadcrumb, the bit-trace dump that follows it when <see cref="Tracing.Enabled" /> is
    ///     armed, and the typed-wrapper bootstrap warning. Defaults to
    ///     <see cref="Console.WriteLine(string)" />, which is what this tracker has always done.
    ///     <para>
    ///         Set it to redirect (a batch job collecting per-demo logs) or to silence
    ///         (<c>sink = _ =&gt; { }</c>) — per tracker instance, so one parse in a batch can be
    ///         silenced without affecting the others. The sink may be invoked from whichever thread
    ///         is driving the replay, and consecutive lines of one report are emitted under a
    ///         process-wide lock so parallel workers do not interleave.
    ///     </para>
    ///     <para>
    ///         This is the prose stream. For structured, per-error consumption use
    ///         <see cref="DecodeErrorRaised" /> (typed <see cref="DecodeError" /> records) or read
    ///         <see cref="LastEntityError" />; those are unaffected by the sink.
    ///     </para>
    /// </summary>
    public Action<string> DecodeDiagnosticSink { get; set; } = Console.WriteLine;

    /// <summary>
    ///     Fired once per FHDR_ENTERPVS in svc_PacketEntities — i.e., every time the wire signals
    ///     a new entity entering the slot. Useful for diagnostics: a healthy parser fires this once
    ///     per real entity creation; bit-misaligned decoding produces phantom firings.
    /// </summary>
    public event Action<int, EntityState>? EntityCreated;

    /// <summary>Fired when an entity's fields change. (entityIndex, state)</summary>
    public event Action<int, EntityState>? EntityUpdated;

    /// <summary>
    ///     Fired right after each CSVCMsg_PacketEntities is processed (or fails). Args:
    ///     (packetCount after processing, decodeErrorJustHappened, deltaUnknownDelta since previous packet).
    ///     The UI debugger subscribes to check Tier 3 breakpoints. Stays unfired when the
    ///     tracker is used via <see cref="PeekEntityUpdates" /> (peek doesn't mutate _packetCount).
    /// </summary>
    public event Action<int, bool, int>? PacketProcessed;

    // ── Read-only entity_data peek (for display) ─────────────────────────────

    /// <summary>
    ///     Decodes <paramref name="msg" />'s entity_data bit stream using the current schema
    ///     and returns one <see cref="EntityUpdateInfo" /> per entity update, without
    ///     mutating <see cref="CurrentEntities" />.
    ///     Returns <c>null</c> when schema or class info is not yet available.
    /// </summary>
    public List<EntityUpdateInfo>? PeekEntityUpdates(CSVCMsg_PacketEntities msg)
    {
        if (Schema is null || _classIdToName.Count == 0)
        {
            return null;
        }

        if (msg.EntityData.IsEmpty)
        {
            return null;
        }

        List<EntityUpdateInfo> result = new(msg.UpdatedEntries);

        // Peek's own field-path scratch — deliberately NOT the Replay buffer (_fieldPathScratch), so a
        // UI peek can never disturb an in-flight Replay decode (mirrors the _trace isolation).
        List<FieldPath> peekScratch = new(32);

        try
        {
            BitBuffer buf = new(msg.EntityData.ToByteArray());
            int entityIndex = -1;

            for (int i = 0; i < msg.UpdatedEntries; i++)
            {
                entityIndex += 1 + (int)buf.ReadUBitVar();
                if ((uint)entityIndex >= MaxEdicts)
                {
                    break;
                }

                uint updateFlags = buf.ReadUBits(2);
                bool leavePvs = (updateFlags & 0b01) != 0;
                bool enterPvs = (updateFlags & 0b10) != 0;

                if (leavePvs)
                {
                    EntityState? live = CurrentEntities[entityIndex];
                    result.Add(new EntityUpdateInfo
                    {
                        Kind = EntityUpdateInfo.UpdateType.Leave,
                        EntityIndex = entityIndex,
                        ClassName = live?.ClassName ?? "",
                        Serial = live?.Serial ?? 0
                    });
                    continue;
                }

                if (enterPvs)
                {
                    uint classId = buf.ReadUBits(_serverClassBits > 0 ? _serverClassBits : 10);
                    uint serialNum = buf.ReadUBits(NumSerialNumberBits);
                    buf.ReadUVarInt32(); // skip unknown spawngroup field

                    if (!_classIdToName.TryGetValue((int)classId, out string? className))
                    {
                        className = $"UnknownClass({classId})";
                    }

                    EntityState temp = new(className, (int)serialNum);

                    if (_instanceBaselines.TryGetValue((int)classId, out byte[]? baseline))
                    {
                        BitBuffer baselineBuf = new(baseline);
                        ReadEntityFields(ref baselineBuf, temp, peekScratch);
                    }

                    ReadEntityFields(ref buf, temp, peekScratch);

                    result.Add(new EntityUpdateInfo
                    {
                        Kind = EntityUpdateInfo.UpdateType.Enter,
                        EntityIndex = entityIndex,
                        ClassName = className,
                        Serial = (int)serialNum,
                        Fields = temp.Fields
                    });
                }
                else
                {
                    // Delta update — only changed fields
                    EntityState? live = CurrentEntities[entityIndex];
                    string clsName = live?.ClassName ?? $"entity#{entityIndex}";
                    int serial = live?.Serial ?? 0;

                    EntityState temp = new(clsName, serial);
                    ReadEntityFields(ref buf, temp, peekScratch);

                    result.Add(new EntityUpdateInfo
                    {
                        Kind = EntityUpdateInfo.UpdateType.Delta,
                        EntityIndex = entityIndex,
                        ClassName = clsName,
                        Serial = serial,
                        Fields = temp.Fields
                    });
                }
            }
        }
        catch
        {
            // Return partial results on decode error (schema mismatch, corrupt data, etc.)
            if (result.Count == 0)
            {
                return null;
            }
        }

        return result;
    }

    // ── Public entry point ────────────────────────────────────────────────────

    /// <summary>
    ///     Replays all frames in order. After this returns, <see cref="CurrentEntities" />
    ///     reflects the final tick of the demo.
    /// </summary>
    public void Replay(IReadOnlyList<DemoFrame> frames)
    {
        foreach (DemoFrame frame in frames)
        {
            ProcessFrame(frame);
        }
    }

    /// <summary>
    ///     Clears the live entity set while RETAINING the loaded schema (serializer, class registry,
    ///     instance baselines, server class-bit width). This is how a Track-4 parallel checkpoint worker
    ///     starts: replay the minimal signon prefix to load the schema, drop the entities that prefix
    ///     created, then seed state from a <c>DEM_FullPacket</c> snapshot via
    ///     <see cref="ProcessFullPacketCheckpoint" />.
    /// </summary>
    public void ResetEntitiesKeepSchema() => CurrentEntities.Clear();

    /// <summary>
    ///     Seeds entity state from a <c>DEM_FullPacket</c>'s bundled snapshot — its
    ///     <c>CDemoStringTables</c> (full string-table / instance-baseline snapshot) and the nested
    ///     packet's <c>CSVCMsg_PacketEntities</c> (a full ENTERPVS snapshot of every in-PVS entity).
    ///     Unlike normal sequential playback (<c>ProcessFrame</c>), this does NOT skip the
    ///     <c>PacketEntities</c>: sequential playback skips a full packet's entities as redundant because
    ///     the delta stream already built the state, but a checkpoint worker has no prior deltas and
    ///     needs the snapshot to reconstruct it. Process this against a schema-loaded, entities-empty
    ///     tracker (see <see cref="ResetEntitiesKeepSchema" />). Used by the parallel decode.
    /// </summary>
    public void ProcessFullPacketCheckpoint(DemoFrame fullPacketFrame)
    {
        CurrentTick = fullPacketFrame.ServerTick;
        CurrentFrameIndex = fullPacketFrame.FrameNumber;
        foreach (NetMessage msg in fullPacketFrame.InnerMessages)
        {
            ProcessNetMessage(msg);
        }
    }

    /// <summary>
    ///     Seeds <see cref="_instanceBaselines" /> from a <c>DEM_FullPacket</c> frame's bundled
    ///     <c>CDemoStringTables</c> <c>instancebaseline</c> snapshot (DEM-form: each item is already an
    ///     unpacked <c>{ str = classId, data = baseline bytes }</c> pair, unlike the bit-packed
    ///     <c>CSVCMsg_UpdateStringTable</c> blob that <see cref="ReadInstanceBaselineUpdate" /> parses).
    ///     Returns <c>true</c> iff the frame carried an <c>instancebaseline</c> table.
    ///     <para>
    ///         The normal net-message dispatch ignores <c>CDemoStringTables</c>: sequential playback builds
    ///         instancebaseline incrementally from the <c>CSVCMsg_Create/UpdateStringTable</c> stream. A
    ///         Track-4 checkpoint worker skipped that stream, so it must seed instancebaseline from a
    ///         snapshot, or entities CREATED after the checkpoint (a mid-chunk ENTERPVS) decode without
    ///         their per-class baseline fields. <b>The full-packet string-table dump is INCREMENTAL</b> — a
    ///         given table appears only when it changed since the previous full packet, and when it appears
    ///         it is the COMPLETE current table. So a caller that lands on a full packet whose snapshot
    ///         omits <c>instancebaseline</c> must walk back to the most recent one that carried it (the
    ///         table was unchanged in between). Checkpoint-only; never runs on the sequential
    ///         <c>ProcessFrame</c> path, so sequential decode stays byte-identical. Merges (upsert) rather
    ///         than replaces, so any prefix-loaded baseline a sparser snapshot omits is retained.
    ///     </para>
    /// </summary>
    public bool LoadInstanceBaselineSnapshot(DemoFrame fullPacketFrame)
    {
        bool loaded = false;
        foreach (NetMessage msg in fullPacketFrame.InnerMessages)
        {
            if (msg.Payload is not CDemoStringTables snapshot)
            {
                continue;
            }

            foreach (CDemoStringTables.Types.table_t table in snapshot.Tables)
            {
                if (table.TableName != "instancebaseline")
                {
                    continue;
                }

                loaded = true;
                foreach (CDemoStringTables.Types.items_t item in table.Items)
                {
                    if (string.IsNullOrEmpty(item.Str) || item.Data.IsEmpty)
                    {
                        continue;
                    }

                    // classId-key handling mirrors ReadInstanceBaselineUpdate: "classId" or
                    // "classId:altBaseline", Baseline 0 only.
                    if (int.TryParse(item.Str.Split(':')[0], out int classId))
                    {
                        _instanceBaselines[classId] = item.Data.ToByteArray();
                    }
                }
            }
        }

        return loaded;
    }

    /// <summary>
    ///     Returns a snapshot of all current entity fields (slotIndex → {fieldKey → value}).
    /// </summary>
    public Dictionary<int, Dictionary<string, object?>> SnapshotCurrentFields()
        => CurrentEntities.Snapshot();

    /// <summary>Append one trace entry, but only while a Replay packet is in flight.</summary>
    private void AddTrace(in DecodeTraceEntry entry)
    {
        if (_traceContextActive)
        {
            _trace.Add(entry);
        }
    }

    private static LazyArrayElementDescs BuildArrayElementDescs(RuntimeField field, string arrayPath)
    {
        // Each element of an array-of-class is itself a Ptr-like slot: a length-1 path on
        // the element reads 1 bit (isSet). Wire shape for elements is always Ptr per
        // demofile-net's per-class generated decoders.
        FieldDecoder? elementLengthOneDecoder = (ref b) =>
        {
            b.ReadOneBit();
            return null!;
        };

        // For arrays of primitives, build the element decoder once from the inner element type
        // (NOT the outer container TypeName, which would resolve to "CNetworkUtlVectorBase" and
        // dispatch to Fallback.ReadUVarInt32 — masking the real per-element type).
        IntDecoder? intDec = null;
        FloatDecoder? floatDec = null;
        FieldDecoder? boxed = null;
        RuntimeField? elemField = null;
        if (field.ChildSerializer is null)
        {
            elemField = CloneAsElementField(field, GetArrayElementType(field.TypeName));
            intDec = FieldDecoderFactory.TryCreateInt(elemField);
            floatDec = intDec is null ? FieldDecoderFactory.TryCreateFloat(elemField) : null;
            boxed = intDec is null && floatDec is null ? FieldDecoderFactory.Create(elemField) : null;
        }

        return new LazyArrayElementDescs(ArrayPregenSize, e =>
        {
            string elemPath = $"{arrayPath}[{e}]";
            if (field.ChildSerializer is not null)
            {
                // Array elements stay on the Fallback lane — pass shapeBuilder=null
                // so the recursive descriptor build doesn't allocate slots inside an
                // array. Per-element flat-slotting would blow up the lane width for
                // hot containers like m_pWeaponServices.m_hMyWeapons[0..63] without
                // any analysis consumer needing it.
                List<FieldDescriptor> childDescs = BuildFieldDescs(field.ChildSerializer, elemPath, null);
                return new FieldDescriptor(elemPath, elementLengthOneDecoder, childDescs)
                {
                    Field = field
                };
            }

            if (intDec is not null)
            {
                return new FieldDescriptor(elemPath, intDec, null)
                {
                    Field = elemField
                };
            }

            if (floatDec is not null)
            {
                return new FieldDescriptor(elemPath, floatDec, null)
                {
                    Field = elemField
                };
            }

            return new FieldDescriptor(elemPath, boxed!, null)
            {
                Field = elemField
            };
        });
    }

    /// <summary>
    ///     Builds the per-serializer descriptor tree and, when <paramref name="shapeBuilder" />
    ///     is non-null, threads slot allocations through the non-array spine into the
    ///     <see cref="ClassShape" /> being constructed.
    ///     <para>
    ///         The <paramref name="shapeBuilder" /> is propagated through nested-object
    ///         recursion (so <c>m_pWeaponServices.m_hActiveWeapon</c> on
    ///         <c>CCSPlayerPawn</c> gets its slot recorded against the pawn's shape) but
    ///         is NOT passed to array-element builders (array elements stay on the
    ///         Fallback lane to bound slot count and keep <c>m_hMyWeapons[0..63]</c>
    ///         analysis loops working unchanged).
    ///     </para>
    ///     <para>
    ///         <paramref name="lensResolver" /> is the Lens injection hook. When
    ///         non-null, the resolver is consulted at every leaf on the non-array spine to
    ///         override the plain decoder-kind classification with the Lens-declared
    ///         <c>(lane, transform, fallbackDefault)</c>. <paramref name="serializerName" />
    ///         is the top-level class name used as the resolver's first lookup key — it
    ///         does NOT change as the walk recurses into nested objects (sub-services
    ///         flatten under the same class entry on the wire).
    ///     </para>
    /// </summary>
    private static List<FieldDescriptor> BuildFieldDescs(
        RuntimeSerializer ser,
        string prefix,
        ClassShapeBuilder? shapeBuilder,
        LensResolver? lensResolver = null,
        string? serializerName = null)
    {
        List<FieldDescriptor> result = new(ser.Fields.Length);

        for (int i = 0; i < ser.Fields.Length; i++)
        {
            RuntimeField field = ser.Fields[i];
            bool isArray = field.IsArray;

            // Build dot-separated path string
            string path = prefix.Length > 0 ? $"{prefix}.{field.Name}" : field.Name;

            if (field.ChildSerializer is not null && !isArray)
            {
                // Nested object — build child descriptors recursively. Attach a length-1 decoder
                // matching the field's wire shape (Ptr=1bit, PolymorphicPtr=1bit+UBitVar). Without
                // this, length-1 paths on the wire would silently consume 0 bits while the wire
                // actually carries the isSet bit, cascading into bit-misalignment.
                //
                // Pass the builder through — sub-entity leaves (e.g. m_pWeaponServices.m_hActiveWeapon)
                // are addressable by the consumer and deserve their own lane slots.
                List<FieldDescriptor> childDescs = BuildFieldDescs(field.ChildSerializer, path, shapeBuilder, lensResolver, serializerName);
                FieldDecoder? lengthOneDecoder = FieldDecoderFactory.CreateLengthOneDecoder(field);
                result.Add(new FieldDescriptor(path, lengthOneDecoder, childDescs)
                {
                    Field = field
                });
            }
            else if (isArray && field.ChildSerializer is not null)
            {
                // Array of nested objects — length-1 path on the array slot is a Vector resize
                // (UVarInt32), length-2+ indexes into an element via path[next]. The container's
                // length-1 decoder consumes the resize uvarint; per-element length-1 decoders are
                // built inside BuildArrayElementDescs.
                FieldDecoder? lengthOneDecoder = FieldDecoderFactory.CreateLengthOneDecoder(field);
                result.Add(new FieldDescriptor(path, lengthOneDecoder, BuildArrayElementDescs(field, path))
                {
                    Field = field
                });
            }
            else if (isArray)
            {
                // Array of primitives — prefer typed decoders to avoid boxing per element. The
                // array container's own length-1 path is a Vector resize (UVarInt32), same shape
                // as the array-of-class branch above. Element type comes from the inner generic
                // (CNetworkUtlVectorBase<T> → T) — passing the outer container type to TryCreateInt/
                // TryCreateFloat would return null and dispatch every element to Fallback.ReadUVarInt32.
                FieldDecoder? lengthOneDecoder = FieldDecoderFactory.CreateLengthOneDecoder(field);
                RuntimeField elemField = CloneAsElementField(field, GetArrayElementType(field.TypeName));
                IntDecoder? intDec = FieldDecoderFactory.TryCreateInt(elemField);
                FloatDecoder? floatDec = intDec is null ? FieldDecoderFactory.TryCreateFloat(elemField) : null;
                if (intDec is not null)
                {
                    result.Add(new FieldDescriptor(path, lengthOneDecoder, BuildTypedIntArrayDescs(path, intDec, elemField))
                    {
                        Field = field
                    });
                }
                else if (floatDec is not null)
                {
                    result.Add(new FieldDescriptor(path, lengthOneDecoder, BuildTypedFloatArrayDescs(path, floatDec, elemField))
                    {
                        Field = field
                    });
                }
                else
                {
                    result.Add(new FieldDescriptor(path, lengthOneDecoder, BuildPrimitiveArrayDescs(field, path, FieldDecoderFactory.Create(elemField), elemField))
                    {
                        Field = field
                    });
                }
            }
            else if (field.IsFixedArray)
            {
                // Fixed-size array T[N]. Two wire shapes:
                //   - char[N] is sent as a single length-prefixed UTF-8 string at length-1 (NOT
                //     element-by-element). Demofile-net special-cases this to ReadStringUtf8().
                //   - All other T[N] use length-2 paths with path[1] as element index; length-1
                //     is unreachable on the wire.
                int openBracket = field.TypeName.IndexOf('[');
                string elemType = openBracket > 0 ? field.TypeName[..openBracket] : field.TypeName;
                if (elemType == "char")
                {
                    // Synthesize a leaf string field so the existing factory builds a Str decoder.
                    // The decoded value is a string — it lives on the object lane.
                    RuntimeField strField = new(
                        field.Name, "char", field.Encoder,
                        field.BitCount, field.LowValue, field.HighValue,
                        field.EncodeFlags,
                        null, field.ChildSerializerVersion,
                        field.SendNode, field.PolymorphicTypes, field.VarSerializerName);
                    SlotAddr addr = shapeBuilder?.Allocate(LaneKind.Object, path) ?? SlotAddr.Fallback;
                    result.Add(new FieldDescriptor(path, FieldDecoderFactory.Create(strField), null)
                    {
                        Field = strField,
                        SlotAddr = addr
                    });
                }
                else
                {
                    result.Add(new FieldDescriptor(path, decoder: null, BuildFixedArrayElementDescs(field, path))
                    {
                        Field = field
                    });
                }
            }
            else
            {
                // Leaf field — prefer typed decoder to avoid boxing on hot path. Slot lane
                // mirrors the decoder kind: IntDecoder→Int, FloatDecoder→Float, else→Object.
                // The wrapper getter is responsible for any type coercion (bool→int, handle
                // mask, etc.).
                IntDecoder? intDec = FieldDecoderFactory.TryCreateInt(field);
                FloatDecoder? floatDec = intDec is null ? FieldDecoderFactory.TryCreateFloat(field) : null;

                // Classify the natural decoder lane from the factory output.
                LaneKind naturalLane = intDec is not null ? LaneKind.Int
                    : floatDec is not null ? LaneKind.Float
                    : LaneKind.Object;

                // Consult the Lens resolver to override the natural lane with the
                // Lens-declared one. Resolver returns null for unmapped paths
                // (plain decoder-kind classification) — see LensTransform.cs.
                LensSlotRule? rule = lensResolver?.Invoke(serializerName ?? "", path);

                // Locked V1 decisions:
                //
                // (a) HandleIndex transform is declared on the Lens entry but **treated as None
                //     for the lane value**. The raw wire integer (UInt64 / UInt32 / Int32) lands
                //     on the lane unchanged so Fields["m_hController"] continues to return the
                //     raw boxed integer (which PawnLookup.TryUnboxHandle already handles). The
                //     wrapper's typed getter does the masking + sentinel
                //     checks at access time. Mechanically: route to natural decoder lane.
                //
                // (b) For Transform.None / Transform.BoolFromInt with lane drift (Lens declared
                //     a different lane than the wire produces — e.g. a uint64-wire m_steamID
                //     declared as IntLane in the genesis), **honour the wire**. The
                //     value preserves precision on its natural lane; the typed wrapper getter
                //     can adapt. Truncating uint64 → int here would silently drop the top
                //     32 bits of every SteamID.
                //
                // (c) Only explicit coercion transforms (CastToInt / CastToFloat / CastToUInt64)
                //     drive lane drift in V1. Those are exactly the transforms a `typeShift`
                //     migration emits to document the intentional re-routing.
                LensTransform transform = rule?.Transform ?? LensTransform.None;
                LaneKind targetLane = rule is { } r
                                      && transform != LensTransform.HandleIndex
                                      && (r.Lane == naturalLane || IsCoercionTransform(transform))
                    ? r.Lane
                    : naturalLane;
                object? fallbackDefault = rule?.FallbackDefault;
                // When the Lens rule supplies a codegen-emitted slot index, honour it
                // so the codegen-emitted wrapper layout is
                // authoritative. Sentinel -1 (the default when no rule, or when a rule
                // pre-dates the codegen-slot emission) routes to the auto-increment
                // path — zero behaviour change for the backward-compat ride-out.
                int lensSlot = rule?.LensSlot ?? -1;
                if (targetLane != LaneKind.Fallback && shapeBuilder is not null)
                {
                    SlotAddr addr = shapeBuilder.Allocate(targetLane, path, transform, fallbackDefault, lensSlot);
                    AddLeafDescriptor(result, field, path, intDec, floatDec, naturalLane, addr, transform);
                }
                else
                {
                    // No shape builder (we're inside an array element walk) — emit a fallback
                    // descriptor and let the lane-write site route to _fallback.
                    SlotAddr addr = SlotAddr.Fallback;
                    AddLeafDescriptor(result, field, path, intDec, floatDec, naturalLane, addr, transform);
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Builds the leaf descriptor for the given field metadata and adds it to
    ///     <paramref name="result" />. Preserves the existing typed-decoder fast path
    ///     (Int / Float / Object) but stamps the per-leaf <see cref="SlotAddr" /> and
    ///     <see cref="LensTransform" /> the Lens resolver returned. The descriptor's
    ///     <c>SlotAddr.Lane</c> is the lane chosen by the Lens resolver (or the natural
    ///     decoder lane when unresolved); the descriptor's <c>Kind</c> stays the natural
    ///     decoder kind so <see cref="ReadAndTrace" /> still dispatches to the typed path.
    ///     When the two disagree (lane drift), <see cref="ReadAndTrace" /> coerces the
    ///     decoded value into the target lane.
    /// </summary>
    private static void AddLeafDescriptor(
        List<FieldDescriptor> result,
        RuntimeField field,
        string path,
        IntDecoder? intDec,
        FloatDecoder? floatDec,
        LaneKind naturalLane,
        SlotAddr addr,
        LensTransform transform)
    {
        if (intDec is not null)
        {
            result.Add(new FieldDescriptor(path, intDec, null)
            {
                Field = field,
                SlotAddr = addr,
                Transform = transform
            });
        }
        else if (floatDec is not null)
        {
            result.Add(new FieldDescriptor(path, floatDec, null)
            {
                Field = field,
                SlotAddr = addr,
                Transform = transform
            });
        }
        else
        {
            result.Add(new FieldDescriptor(path, FieldDecoderFactory.Create(field), null)
            {
                Field = field,
                SlotAddr = addr,
                Transform = transform
            });
        }

        // naturalLane is reserved for future use by ReadAndTrace; suppress the unused warning.
        _ = naturalLane;
    }

    private static LazyArrayElementDescs BuildFixedArrayElementDescs(RuntimeField field, string arrayPath)
    {
        int openBracket = field.TypeName.IndexOf('[');
        int closeBracket = field.TypeName.IndexOf(']');
        int size = openBracket > 0 && closeBracket > openBracket
                                   && int.TryParse(field.TypeName.AsSpan(openBracket + 1, closeBracket - openBracket - 1), out int n)
            ? n
            : ArrayPregenSize;

        RuntimeField elemField = CloneAsElementField(field, GetArrayElementType(field.TypeName));

        IntDecoder? intDec = FieldDecoderFactory.TryCreateInt(elemField);
        FloatDecoder? floatDec = intDec is null ? FieldDecoderFactory.TryCreateFloat(elemField) : null;
        FieldDecoder fallback = FieldDecoderFactory.Create(elemField);

        int pregen = Math.Max(size, ArrayPregenSize);
        return new LazyArrayElementDescs(pregen, e =>
        {
            string elemPath = $"{arrayPath}[{e}]";
            if (intDec is not null)
            {
                return new FieldDescriptor(elemPath, intDec, null)
                {
                    Field = elemField
                };
            }

            if (floatDec is not null)
            {
                return new FieldDescriptor(elemPath, floatDec, null)
                {
                    Field = elemField
                };
            }

            return new FieldDescriptor(elemPath, fallback, null)
            {
                Field = elemField
            };
        });
    }

    private static LazyArrayElementDescs BuildPrimitiveArrayDescs(RuntimeField field, string arrayPath, FieldDecoder decoder, RuntimeField elemField)
    {
        return new LazyArrayElementDescs(ArrayPregenSize,
            e => new FieldDescriptor($"{arrayPath}[{e}]", decoder, null)
            {
                Field = elemField
            });
    }

    private static LazyArrayElementDescs BuildTypedFloatArrayDescs(string arrayPath, FloatDecoder floatDecoder, RuntimeField elemField)
    {
        return new LazyArrayElementDescs(ArrayPregenSize,
            e => new FieldDescriptor($"{arrayPath}[{e}]", floatDecoder, null)
            {
                Field = elemField
            });
    }

    private static LazyArrayElementDescs BuildTypedIntArrayDescs(string arrayPath, IntDecoder intDecoder, RuntimeField elemField)
    {
        return new LazyArrayElementDescs(ArrayPregenSize,
            e => new FieldDescriptor($"{arrayPath}[{e}]", intDecoder, null)
            {
                Field = elemField
            });
    }

    /// <summary>Synthesizes a RuntimeField describing one element of an array container.</summary>
    private static RuntimeField CloneAsElementField(RuntimeField field, string elementType) =>
        new(
            field.Name, elementType, field.Encoder,
            field.BitCount, field.LowValue, field.HighValue,
            field.EncodeFlags,
            field.ChildSerializerName, field.ChildSerializerVersion,
            field.SendNode, field.PolymorphicTypes, field.VarSerializerName);

    /// <summary>
    ///     First-error report, emitted on <see cref="DecodeDiagnosticSink" />. The breadcrumb is
    ///     ALWAYS-ON (it costs nothing on the healthy path — it runs only on an exception) so even a
    ///     default (un-flagged) run tells the user a decode error happened, where, and how to get
    ///     the full bit-trace. It reads only EXISTING state (no per-op work): the in-flight class +
    ///     <c>ex.Message</c> (which carries "current path=…" via the re-throw at
    ///     <c>ReadEntityFields</c>). The full trace dump runs only when tracing was armed
    ///     (<see cref="Tracing.Enabled" />). The whole block is serialized so parallel workers'
    ///     lines don't interleave (see the lock's docs).
    /// </summary>
    private void ReportFirstDecodeError(Exception ex, int entityDataBits)
    {
        string breadcrumbClass = _curEntityIndex >= 0 && CurrentEntities[_curEntityIndex] is { } crumbLive
            ? crumbLive.ClassName
            : "";
        lock (_decodeErrorConsoleLock)
        {
            DecodeDiagnosticSink($"[EntityTracker] first decode error at packet#{PacketCount}: {ex.GetType().Name}: {ex.Message}");
            DecodeDiagnosticSink($"  entity#{_curEntityIndex} class={(breadcrumbClass.Length > 0 ? breadcrumbClass : "<unknown>")}");
            DecodeDiagnosticSink($"  entity_data total bits: {entityDataBits}; delta-on-unknown so far: {DeltaUnknownCount}");
            if (_traceContextActive)
            {
                DumpTrace();
            }
            else
            {
                DecodeDiagnosticSink("  [trace off] re-run with CS2DEMOKIT_TRACE_DECODE=1 for the full decode bit-trace (decode is deterministic in the demo bytes, so the failure reproduces).");
            }
        }
    }

    /// <summary>
    ///     Test-only entry to <see cref="ReportFirstDecodeError" />. A synthetic decode failure
    ///     cannot be manufactured from outside — <c>BitBuffer</c> zero-pads past end-of-span rather
    ///     than throwing, so truncating <c>EntityData</c> does not reliably raise anything (the same
    ///     reason <c>DecodeTraceGateTests</c> is a gate-COUNT test). This seam lets the sink-
    ///     redirection test drive the real report instead of a re-implementation of it. Internal,
    ///     surfaced via InternalsVisibleTo — mirrors the <see cref="TraceEntryCountForTest" />
    ///     precedent.
    /// </summary>
    /// <param name="ex">Stands in for the exception a corrupt packet would have thrown.</param>
    /// <param name="entityDataBits">Stands in for the failing packet's <c>entity_data</c> bit count.</param>
    /// <param name="withTraceDump">
    ///     Simulates the report running with the trace armed, so the test can cover
    ///     <see cref="DumpTrace" />'s output as well as the three breadcrumb lines. Normally set by
    ///     <see cref="ProcessPacketEntities" /> for the duration of a packet.
    /// </param>
    internal void ReportFirstDecodeErrorForTest(Exception ex, int entityDataBits, bool withTraceDump = false)
    {
        bool prior = _traceContextActive;
        _traceContextActive = withTraceDump;
        try
        {
            ReportFirstDecodeError(ex, entityDataBits);
        }
        finally
        {
            _traceContextActive = prior;
        }
    }

    /// <summary>
    ///     Tripwire for the order-sensitive typed-wrapper bootstrap. Wrapper factories are
    ///     only meaningful when a Schema Lens resolver has been bound BEFORE any wire data flows —
    ///     the resolver is what routes each leaf onto the lane the generated wrapper reads, and
    ///     <c>BuildFieldDescs</c> consults it once per class and never re-classifies. Factories with
    ///     no resolver is therefore a silent-wrong configuration: decoding succeeds, and the
    ///     wrappers return defaults or values off the wrong lane.
    ///     <para>
    ///         Checked at the first packet-entities decode, which is precisely when the omission
    ///         becomes consequential and also catches a resolver bound too late (by then the first
    ///         decode has already been classified without it). One-shot per tracker. Deliberately
    ///         gated on there being at least one registered factory, so the dict-only path — a bare
    ///         <c>new EntityTracker()</c> with neither lens nor wrappers, which is a perfectly good
    ///         way to use this class — stays silent.
    ///     </para>
    /// </summary>
    private void WarnIfWrappersRegisteredWithoutLens()
    {
        if (_wrapperBootstrapWarned || _lensResolver is not null || _entityFactories.Count == 0)
        {
            return;
        }

        _wrapperBootstrapWarned = true;
        DecodeDiagnosticSink(
            $"[EntityTracker] {_entityFactories.Count} typed-wrapper factories are registered but no Schema Lens "
            + "resolver is bound — wrapper properties will read wrong or default values. Build the tracker with "
            + "EntityTrackerFactory.CreateCurated(), or bind the resolver before the first frame.");
    }

    /// <summary>
    ///     Writes the trace ring buffer to console after a packet-level decode error. Emits:
    ///     (a) the full chronological log of the last packet (path ops + field reads), and
    ///     (b) an "outliers" view sorted by |bitsConsumed - expectedBits|, where expected is
    ///     heuristically derived from RuntimeField (BitCount when non-zero, otherwise 1 or
    ///     varint-ish). The bad field's outlier rank is usually obvious without needing
    ///     exact expected-bits per encoder.
    /// </summary>
    private void DumpTrace()
    {
        if (_trace.Count == 0)
        {
            DecodeDiagnosticSink("[EntityTracker] trace buffer empty");
            return;
        }

        DecodeDiagnosticSink($"[EntityTracker] === decode trace ({_trace.Count} entries, last packet) ===");
        int start = Math.Max(0, _trace.Count - 400); // cap chronological output to last 400 entries
        if (start > 0)
        {
            DecodeDiagnosticSink($"[EntityTracker] (showing last 400 of {_trace.Count}; full set retained for outlier analysis)");
        }

        for (int i = start; i < _trace.Count; i++)
        {
            DecodeTraceEntry e = _trace[i];
            switch (e.Kind)
            {
                case TraceKind.BeginEntity:
                    DecodeDiagnosticSink($"  --- entity#{e.Entity} {e.UpdateKind} {e.ClassName} bitPos={e.BitPosBefore} ---");
                    break;
                case TraceKind.Prelude:
                    DecodeDiagnosticSink($"    [{i,4}] PRELUDE  entity#{e.Entity} {e.UpdateKind,-14} bits={e.BitsConsumed,3} pos={e.BitPosBefore} {e.ClassName}");
                    break;
                case TraceKind.PathOp:
                    DecodeDiagnosticSink($"    [{i,4}] op#{e.OpIndex,-3} {e.OpName,-40} bits={e.BitsConsumed,3} pos={e.BitPosBefore} path={e.Path}");
                    break;
                case TraceKind.NestedDescent:
                    DecodeDiagnosticSink($"    [{i,4}]   nested → {e.Path} ({e.TypeName})");
                    break;
                case TraceKind.FieldRead:
                    DecodeDiagnosticSink($"    [{i,4}] READ            {e.Path,-60} bits={e.BitsConsumed,3} pos={e.BitPosBefore} type={e.TypeName} enc={e.Encoder ?? "-"} bc={e.BitCount} ef={e.EncodeFlags}");
                    break;
            }
        }

        // Outliers: rank field reads by |bitsConsumed - expectedBits|. The perpetrator is the
        // first read whose actual bits stray far from what its metadata implies — that's the
        // wire-shape mismatch we're hunting.
        List<(int Index, DecodeTraceEntry Entry, int Expected, int Delta)> reads = new();
        for (int i = 0; i < _trace.Count; i++)
        {
            DecodeTraceEntry e = _trace[i];
            if (e.Kind != TraceKind.FieldRead)
            {
                continue;
            }

            int expected = EstimateExpectedBits(e);
            int delta = Math.Abs(e.BitsConsumed - expected);
            reads.Add((i, e, expected, delta));
        }

        if (reads.Count > 0)
        {
            DecodeDiagnosticSink($"[EntityTracker] === outliers (|actual-expected| top 30, total {reads.Count} reads) ===");
            reads.Sort((a, b) => b.Delta.CompareTo(a.Delta));
            int limit = Math.Min(30, reads.Count);
            for (int j = 0; j < limit; j++)
            {
                (int Index, DecodeTraceEntry Entry, int Expected, int Delta) r = reads[j];
                DecodeDiagnosticSink($"    Δ={r.Delta,4}  actual={r.Entry.BitsConsumed,3}  expected≈{r.Expected,3}  [{r.Index,4}] {r.Entry.ClassName}::{r.Entry.Path}  type={r.Entry.TypeName} enc={r.Entry.Encoder ?? "-"} bc={r.Entry.BitCount} ef={r.Entry.EncodeFlags}");
            }
        }
    }

    /// <summary>
    ///     Rough lower bound on the bits a field-read SHOULD consume given its declared metadata.
    ///     Used only to rank outliers — exact match isn't required. A fixed BitCount is exact; a
    ///     length-1 Ptr is 1 bit; varint and string have no fixed bit count, treated as 8.
    /// </summary>
    private static int EstimateExpectedBits(in DecodeTraceEntry e)
    {
        if (e.BitCount > 0)
        {
            return e.BitCount;
        }

        if (e.TypeName is null)
        {
            return 1;
        }

        // Common types with characteristic minimum bit shapes
        string t = e.TypeName;
        if (t.EndsWith('*'))
        {
            return 1; // Ptr isSet bit
        }

        if (t == "bool")
        {
            return 1;
        }

        if (t.StartsWith("Color", StringComparison.Ordinal))
        {
            return 32;
        }

        if (t.StartsWith("Vector", StringComparison.Ordinal))
        {
            return 96; // worst case 3×32
        }

        if (t.StartsWith("QAngle", StringComparison.Ordinal))
        {
            return 96;
        }

        if (t == "uint64" || t == "int64")
        {
            return 64;
        }

        if (t == "uint32" || t == "int32")
        {
            return 32;
        }

        if (t == "uint16" || t == "int16")
        {
            return 16;
        }

        if (t == "uint8" || t == "int8")
        {
            return 8;
        }

        if (t == "float32")
        {
            return 32;
        }

        return 8;
    }

    /// <summary>
    ///     Strips the container wrapper from an array TypeName to recover the element type.
    ///     <c>CNetworkUtlVectorBase&lt;float&gt;</c> → <c>float</c>;
    ///     <c>int32[]</c> → <c>int32</c>; <c>T[N]</c> → <c>T</c>.
    /// </summary>
    private static string GetArrayElementType(string typeName)
    {
        int lt = typeName.IndexOf('<');
        if (lt >= 0)
        {
            int gt = typeName.LastIndexOf('>');
            return gt > lt ? typeName.Substring(lt + 1, gt - lt - 1).Trim() : typeName;
        }

        int bracket = typeName.IndexOf('[');
        if (bracket > 0)
        {
            return typeName[..bracket];
        }

        return typeName;
    }

    // ── Field descriptor cache ────────────────────────────────────────────────

    private List<FieldDescriptor>? GetFieldDescriptors(string className)
    {
        if (_fieldDescs.TryGetValue(className, out List<FieldDescriptor>? cached))
        {
            return cached;
        }

        if (Schema is null)
        {
            return null;
        }

        RuntimeSerializer? ser = Schema.GetSerializer(className);
        if (ser is null)
        {
            _fieldDescs[className] = [];
            return null;
        }

        // Cache-MISS branch only (the cache-hit early-return above already fired on every
        // repeat sighting), so this accumulates the cost of actual descriptor builds —
        // roughly one per distinct class — not the millions of per-entity dict lookups.
        bool prof = Profiling.Enabled;
        long dbStart = 0, dbAlloc = 0;
        if (prof)
        {
            _profiled = true;
            _profDescriptorBuilds++;
            dbStart = Stopwatch.GetTimestamp();
            dbAlloc = GC.GetAllocatedBytesForCurrentThread();
        }

        ClassShapeBuilder shapeBuilder = new(className);

        // Slot pre-pass: when a Lens resolver is bound, walk the spine
        // (recursing into non-array ChildSerializers, visiting leaves) and
        // reserve every Lens-pinned slot in shapeBuilder BEFORE any allocation
        // runs. The codegen slot planner emits dense (0..N-1) slots in
        // canonical-sorted order, which has zero correlation with the wire
        // declaration order BuildFieldDescs walks in. Without this pre-pass,
        // an auto-incrementing non-Lens field walked early would claim a low
        // slot a not-yet-walked Lens-pinned field owns, and the later Lens
        // pin would either overwrite the auto-inc field's path metadata
        // (silent key drop from EntityState.Fields) or trip the collision
        // guard in ClassShape.Append.
        if (_lensResolver is not null)
        {
            PreReserveLensSlots(ser, "", shapeBuilder, _lensResolver, className);
        }

        List<FieldDescriptor> descs = BuildFieldDescs(ser, "", shapeBuilder, _lensResolver, className);
        _fieldDescs[className] = descs;
        _classShapes[className] = shapeBuilder.Build();
        if (prof)
        {
            _profDescriptorBuildTicks += Stopwatch.GetTimestamp() - dbStart;
            _profDescriptorBuildAlloc += GC.GetAllocatedBytesForCurrentThread() - dbAlloc;
        }

        return descs;
    }

    /// <summary>
    ///     Spine-only pre-walk that mirrors the leaf-classification flow inside
    ///     <see cref="BuildFieldDescs" /> but produces no descriptors and no
    ///     allocations — only reservations into
    ///     <see cref="ClassShapeBuilder.ReserveLensSlot" /> for every Lens-mapped
    ///     leaf with a codegen-pinned slot. See <c>EnsureFieldDescs</c> for
    ///     why this pre-pass is required.
    ///     <para>
    ///         Recurses through <c>ChildSerializer != null &amp;&amp; !IsArray</c>
    ///         branches only. Array element walks are intentionally skipped because
    ///         <see cref="BuildArrayElementDescs" /> and friends call
    ///         <c>BuildFieldDescs</c> with <c>shapeBuilder: null</c>, so Lens pins
    ///         can never land on an array element — only on the non-array spine.
    ///     </para>
    ///     <para>
    ///         The lane-selection logic mirrors the real walk's drift rules at the
    ///         leaf-classification site to keep the reservation lane in lockstep
    ///         with the lane the real <c>Allocate</c> will route to. Drift cases
    ///         (Lens-declared lane disagrees with the wire's natural lane on a
    ///         non-coercion transform) reserve nothing and the field flows through
    ///         auto-increment on its natural lane.
    ///     </para>
    /// </summary>
    private static void PreReserveLensSlots(
        RuntimeSerializer ser,
        string prefix,
        ClassShapeBuilder shapeBuilder,
        LensResolver lensResolver,
        string serializerName)
    {
        for (int i = 0; i < ser.Fields.Length; i++)
        {
            RuntimeField field = ser.Fields[i];
            bool isArray = field.IsArray;
            string path = prefix.Length > 0 ? $"{prefix}.{field.Name}" : field.Name;

            if (field.ChildSerializer is not null && !isArray)
            {
                // Sub-serializer on the spine — recurse so flattened sub-service
                // paths (e.g. m_pInGameMoneyServices.m_iAccount) are also reserved.
                PreReserveLensSlots(field.ChildSerializer, path, shapeBuilder, lensResolver, serializerName);
                continue;
            }

            if (isArray)
            {
                // Array branches don't carry Lens-pinned leaves on the spine.
                continue;
            }

            if (field.ChildSerializer is not null)
            {
                // Defensive: any other ChildSerializer shape doesn't expose a leaf
                // value at this path.
                continue;
            }

            // Leaf on the spine. Mirror the real walk's leaf classification: ask
            // the Lens resolver for a rule, classify the natural decoder lane,
            // apply the same drift rule, and reserve only if the targetLane
            // matches the Lens-declared lane (otherwise the real Allocate will
            // route the field via auto-inc on the natural lane and ignore the
            // codegen lensSlot — reserving in that case would block an unrelated
            // auto-inc field from claiming the slot).
            LensSlotRule? rule = lensResolver.Invoke(serializerName, path);
            if (rule is null || rule.Value.LensSlot < 0)
            {
                continue;
            }

            IntDecoder? intDec = FieldDecoderFactory.TryCreateInt(field);
            FloatDecoder? floatDec = intDec is null ? FieldDecoderFactory.TryCreateFloat(field) : null;
            LaneKind naturalLane = intDec is not null ? LaneKind.Int
                : floatDec is not null ? LaneKind.Float
                : LaneKind.Object;

            LensSlotRule r = rule.Value;
            LensTransform transform = r.Transform;
            LaneKind targetLane = transform != LensTransform.HandleIndex
                                  && (r.Lane == naturalLane || IsCoercionTransform(transform))
                ? r.Lane
                : naturalLane;

            // Reserve the slot on whichever lane the real Allocate will route
            // to, exactly mirroring the real walk's lane-selection (the real
            // Allocate uses `lensSlot = rule.LensSlot` regardless of drift and
            // dispatches to `targetLane`'s codegenSlots HashSet — so we must
            // do the same here to keep the auto-inc cursor synchronized).
            shapeBuilder.ReserveLensSlot(targetLane, r.LensSlot);
        }
    }

    // ── Public Tier-3 API ────────────────────────────────────────────────────

    /// <summary>
    ///     Live wrapper over the entity in <paramref name="slot" />. Returns <c>null</c>
    ///     when the slot is empty, when no factory has been registered for the entity's
    ///     class, or when the registered factory's wrapper is not assignable to
    ///     <typeparamref name="T" />. The wrapper holds a reference to the live
    ///     <see cref="EntityState" /> — every property read traverses the current lane
    ///     values; cross-tick caches will return stale data.
    ///     <para>
    ///         Generic constraint is <c>class</c> deliberately: the tracker cannot (and
    ///         should not) name a wrapper base type — since the cutover the registered
    ///         factories produce SDK wrappers (<c>CS2OpenDev.Sdk.Entities.EntityWrapper</c>
    ///         subclasses), which the tracker never references; <c>class</c> is the widest
    ///         constraint that keeps the cast in <c>as T</c> meaningful.
    ///     </para>
    /// </summary>
    public T? Get<T>(int slot) where T : class
    {
        EntityState? state = CurrentEntities[slot];
        if (state is null)
        {
            return null;
        }

        if (!_entityFactories.TryGetValue(state.ClassName, out Func<EntityState, EntityTracker, object>? factory))
        {
            return null;
        }

        object wrapper = factory(state, this);
        return wrapper as T;
    }

    /// <summary>
    ///     Eager snapshot of the entity in <paramref name="slot" /> as a typed wrapper over a
    ///     <b>detached frozen copy</b> of the entity state. Unlike
    ///     <see cref="Get{T}" />, the returned wrapper does <b>not</b> alias live state: every
    ///     scalar getter reads a clone taken at call time, so a subsequent replay step that
    ///     mutates the live entity cannot change what this wrapper reports. Returns <c>null</c>
    ///     for empty slots or factory mismatches (same conditions as <see cref="Get{T}" />).
    ///     <para>
    ///         The wrapper wraps a detached <see cref="EntityState.FreezeCopy" />, so every
    ///         scalar getter reads frozen data — that IS the snapshot contract. For a
    ///         tracker-free generic node (class name + frozen fields, no wrapper type) use
    ///         <see cref="SnapshotNode" />. The retired local wrapper layer's
    ///         nested-handle-freeze hook (<c>ISnapshotable.SnapshotInto</c>) was removed with
    ///         it in the SDK cutover — SDK wrappers resolve handles live through their world.
    ///     </para>
    /// </summary>
    public T? Snapshot<T>(int slot) where T : class
    {
        EntityState? live = CurrentEntities[slot];
        if (live is null)
        {
            return null;
        }

        if (!_entityFactories.TryGetValue(live.ClassName, out Func<EntityState, EntityTracker, object>? factory))
        {
            return null;
        }

        // Detach: the wrapper wraps a deep copy, so its scalar getters never alias
        // the live state again (snapshots used to alias live state — that was the bug).
        EntityState frozen = live.FreezeCopy();
        return factory(frozen, this) as T;
    }

    /// <summary>
    ///     Snapshot of the entity in <paramref name="slot" /> as a fully tracker-free
    ///     <see cref="EntitySnapshot" /> generic node carrying the entity's
    ///     <see cref="EntitySnapshot.ClassName" /> and a frozen clone of the
    ///     <see cref="EntityState.Fields" /> projection. Holds no live state or tracker
    ///     reference and is safe to carry across threads. Returns <c>null</c> for an empty
    ///     slot. (The nested-handle-freeze tree the local wrapper layer's
    ///     <c>SnapshotInto</c> overrides once populated was removed with that layer in
    ///     the SDK cutover.)
    /// </summary>
    public EntitySnapshot? SnapshotNode(int slot)
    {
        EntityState? live = CurrentEntities[slot];
        if (live is null)
        {
            return null;
        }

        // Freeze the flat projection up-front (handle fields stay raw boxed ints,
        // exactly as on the live Fields path). Clone into a plain dict so the node
        // holds no reference back into the live state.
        IReadOnlyDictionary<string, object?> liveFields = live.Fields;
        Dictionary<string, object?> frozenFields = new(liveFields.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, object?> kv in liveFields)
        {
            frozenFields[kv.Key] = kv.Value;
        }

        return new EntitySnapshot(live.ClassName, live.Serial, frozenFields);
    }

    /// <summary>
    ///     Resolves a raw wire handle (as it lands on the int lane after the V1
    ///     <see cref="LensTransform.HandleIndex" /> identity transform) into a live
    ///     typed wrapper. Performs the entity-handle mask + sentinel checks (<c>0</c>
    ///     and <c>0xFFFFFFFF</c> are "no entity"), looks up the target slot in
    ///     <see cref="CurrentEntities" />, and dispatches through the factory registry
    ///     to build the wrapper.
    ///     <para>
    ///         The low 14 bits of the handle are the entity slot index; bits 14–16 are
    ///         reserved; bits 17+ carry the serial number used to validate that the
    ///         handle still refers to the same entity (not an entity that has been
    ///         destroyed and recycled into the same slot). For V1 we trust the slot
    ///         lookup; serial validation belongs to the wrapper-side accessors.
    ///     </para>
    /// </summary>
    public T? ResolveHandle<T>(int handle) where T : class
    {
        // Two sentinel encodings cover "no entity":
        //   0          — uninitialised handle
        //   0xFFFFFFFF — explicit "invalid" (e.g. cleared m_hOwnerEntity)
        if (handle == 0 || handle == -1)
        {
            return null;
        }

        int slot = handle & 0x3FFF;

        // A narrower field encodes invalid as all-ones within its own width, not as 0xFFFFFFFF: a
        // dead pawn's m_hController reads 0x00FFFFFF, which masks to index 0x3FFF. That index is
        // the invalid marker, never a real entity. Returning null was previously luck, because the
        // slot happens to be empty; this makes it a guarantee if anything ever occupies 16383.
        if (slot == 0x3FFF)
        {
            return null;
        }

        return Get<T>(slot);
    }

    /// <summary>
    ///     Wire-type introspection hook. Returns the <see cref="RuntimeField" />
    ///     metadata for the given engine field on the given class, whether the field is
    ///     Lens-mapped or routed to fallback. Returns <c>null</c> when the class has no
    ///     descriptors yet (parser hasn't seen it) or when the path doesn't exist in the
    ///     class's schema.
    ///     <para>
    ///         Use case: a downstream consumer iterating <c>entity.Fields</c> can call
    ///         <c>tracker.GetFieldMeta(entity.ClassName, key)?.TypeName</c> to recover
    ///         the wire type for display in the entity debug UI, or to coerce a fallback
    ///         value to the right managed type. One <see cref="Dictionary{TKey,TValue}" />
    ///         lookup; thread-safe with respect to <see cref="GetFieldDescriptors" />.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     True when <paramref name="className" /> has field descriptors — i.e. the parser has
    ///     decoded at least one entity of that class. Lets schema-validation consumers
    ///     distinguish "class not seen yet" (wait) from "field missing on a seen class"
    ///     (schema drift, loud) — <see cref="GetFieldMeta" /> returns null for both.
    /// </summary>
    public bool HasClassDescriptors(string className) => _fieldDescs.ContainsKey(className);

    public RuntimeField? GetFieldMeta(string className, string path)
    {
        if (!_fieldDescs.TryGetValue(className, out List<FieldDescriptor>? descs))
        {
            return null;
        }

        return FindLeafField(descs, path);
    }

    /// <summary>
    ///     Recursive depth-first search for the leaf descriptor whose <c>Path</c>
    ///     equals <paramref name="path" />, returning its <see cref="RuntimeField" />.
    ///     Used by <see cref="GetFieldMeta" /> — string comparisons not on the hot path.
    /// </summary>
    /// <summary>
    ///     Parses the <c>[N]</c> immediately following <paramref name="openBracket" /> in
    ///     <paramref name="path" />. Used to index straight into a lazy array-element list rather than
    ///     enumerating it.
    /// </summary>
    private static bool TryParseElementIndex(string path, int openBracket, out int index)
    {
        index = -1;
        int close = path.IndexOf(']', openBracket + 1);
        return close > openBracket + 1
               && int.TryParse(path.AsSpan(openBracket + 1, close - openBracket - 1),
                   NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static RuntimeField? FindLeafField(IReadOnlyList<FieldDescriptor>? descs, string path)
    {
        if (descs is null)
        {
            return null;
        }

        foreach (FieldDescriptor d in descs)
        {
            if (string.Equals(d.Path, path, StringComparison.Ordinal))
            {
                return d.Field;
            }

            if (d.ChildDescs is { } children)
            {
                // Prune obvious sub-trees: a child's Path always starts with this descriptor's
                // Path + '.' or '['. If the target doesn't share the prefix, skip the recursion.
                if (path.Length > d.Path.Length
                    && path.StartsWith(d.Path, StringComparison.Ordinal)
                    && (path[d.Path.Length] == '.' || path[d.Path.Length] == '['))
                {
                    // Array elements are index-addressed: jump straight to the one the path names
                    // instead of walking (and therefore MATERIALISING) all 1024 lazy entries. Without
                    // this, one entity-inspector lookup would undo the laziness for that array.
                    if (children is LazyArrayElementDescs lazyElems
                        && path[d.Path.Length] == '['
                        && TryParseElementIndex(path, d.Path.Length, out int elemIdx)
                        && elemIdx >= 0 && elemIdx < lazyElems.Count)
                    {
                        FieldDescriptor elem = lazyElems[elemIdx];
                        if (string.Equals(elem.Path, path, StringComparison.Ordinal))
                        {
                            return elem.Field;
                        }

                        RuntimeField? nested = elem.ChildDescs is { } grandchildren
                            ? FindLeafField(grandchildren, path)
                            : null;
                        if (nested is not null)
                        {
                            return nested;
                        }

                        continue;
                    }

                    RuntimeField? hit = FindLeafField(children, path);
                    if (hit is not null)
                    {
                        return hit;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    ///     Binds a Schema Lens resolver. The resolver is consulted at every
    ///     leaf descriptor on the non-array spine during the first <see cref="BuildFieldDescs" />
    ///     walk of each serializer. Pre-walked classes are not retroactively re-classified —
    ///     bind early, before any wire data flows.
    ///     <para>
    ///         The resolver lives outside <see cref="EntityTracker" /> because EntityTracking
    ///         sits below the Entities project in the dependency graph and cannot name
    ///         <c>LensState</c> / <c>GeneratedLensRegistry</c> directly. A caller with
    ///         project references to both (e.g. <c>CS2DemoKit.Parser.Entities.Tests</c>, a
    ///         future analysis bootstrap, or a typed-wrapper consumer) builds the resolver
    ///         around <c>GeneratedLensRegistry.Load()</c>.
    ///     </para>
    ///     <para>
    ///         Pass <c>null</c> to clear a previously-bound resolver. Idempotent — does not
    ///         invalidate the descriptor cache, so a rebind after classes have been walked
    ///         is a no-op for those classes.
    ///     </para>
    /// </summary>
    public void BindLensResolver(LensResolver? resolver) => _lensResolver = resolver;

    /// <summary>
    ///     Registers a typed-wrapper factory for the given <paramref name="className" /> —
    ///     the lookup <c>Get&lt;T&gt;</c> and <c>ResolveHandle&lt;T&gt;</c> dispatch through.
    ///     <c>TrackerEntityWorld.RegisterWrapper</c> installs the SDK-wrapper factories via
    ///     this. Late re-registration replaces the previous factory for that class.
    /// </summary>
    public void RegisterEntityFactory(string className, Func<EntityState, EntityTracker, object> factory)
        => _entityFactories[className] = factory;

    // ── Coercion helpers (lane-drift fallback) ───────────────────────────────

    private static bool IsCoercionTransform(LensTransform t) =>
        t is LensTransform.CastToInt or LensTransform.CastToFloat or LensTransform.CastToUInt64;

    private static int CoerceToInt(object? value)
        => value switch
        {
            null => 0,
            int i => i,
            uint ui => (int)ui,
            long l => (int)l,
            ulong ul => (int)ul,
            short s => s,
            ushort us => us,
            byte b => b,
            sbyte sb => sb,
            bool bo => bo ? 1 : 0,
            float f => (int)f,
            double d => (int)d,
            _ => Convert.ToInt32(value, CultureInfo.InvariantCulture)
        };

    private static float CoerceToFloat(object? value)
        => value switch
        {
            null => 0f,
            float f => f,
            double d => (float)d,
            int i => i,
            uint ui => ui,
            long l => l,
            ulong ul => ul,
            _ => Convert.ToSingle(value, CultureInfo.InvariantCulture)
        };

    /// <summary>
    ///     Returns the per-class Schema Lens shape for <paramref name="className" />, building it on
    ///     first request. Lazy: depends on Schema and the descriptor cache being primed via
    ///     <see cref="GetFieldDescriptors" />.
    /// </summary>
    private ClassShape? GetClassShape(string className)
    {
        if (_classShapes.TryGetValue(className, out ClassShape? shape))
        {
            return shape;
        }

        // Force descriptor build (side-effect: also populates _classShapes).
        GetFieldDescriptors(className);
        return _classShapes.GetValueOrDefault(className);
    }

    // ── DEM_ClassInfo ─────────────────────────────────────────────────────────

    private void ProcessClassInfo(CDemoClassInfo msg)
    {
        _classIdToName.Clear();
        foreach (CDemoClassInfo.Types.class_t? cls in msg.Classes)
        {
            _classIdToName[cls.ClassId] = cls.NetworkName;
        }

        // NOTE: Do NOT set _serverClassBits here. The wire bit-count is determined
        // by the SERVER-DECLARED MaxClasses in CSVCMsg_ServerInfo, not by the
        // observed max class ID in the CDemoClassInfo list. The two differ
        // whenever MaxClasses is rounded up to a power of two but the actual
        // class list is shorter — e.g. MaxClasses=512 but max observed classId
        // is 350. The server encodes classIds using log2(512)+1=10 bits, but a
        // ceil(log2(351+1))=9-bit override here misaligns every subsequent
        // ENTERPVS by 1 bit, cascading into garbage classIds → unknown classes
        // → ReadEntityFields early-return without consuming bits → FieldPath
        // overflow inside the next entity's path decode. Matches demofile-net's
        // OnDemoClassInfo which only populates the class registry and never
        // touches _serverClassBits.
    }

    // ── Frame dispatch ────────────────────────────────────────────────────────

    private void ProcessFrame(DemoFrame frame)
    {
        CurrentTick = frame.ServerTick;
        CurrentFrameIndex = frame.FrameNumber;

        // DEM_FullPacket is a seek checkpoint — its embedded CDemoPacket replays state we've
        // already received from earlier DEM_Packet/DEM_SignonPacket frames. Sequential playback
        // must NOT re-process these (they double-deliver PacketEntities, causing duplicate
        // ENTERPVS events and entity-baseline confusion that cascades into bit-misalignment
        // ~5 packets later).
        bool isFullPacketCheckpoint = frame.Command == "DEM_FullPacket";

        foreach (NetMessage msg in frame.InnerMessages)
        {
            if (isFullPacketCheckpoint && msg.Payload is CSVCMsg_PacketEntities)
            {
                continue;
            }

            ProcessNetMessage(msg);
        }
    }

    // ── instancebaseline string table ─────────────────────────────────────────

    /// <summary>
    ///     Applies one instancebaseline string-table update (from either the initial
    ///     <c>CSVCMsg_CreateStringTable</c> or a subsequent <c>CSVCMsg_UpdateStringTable</c>).
    ///     Each entry's key is a class id (<c>"classId"</c> or <c>"classId:altBaseline"</c>) and
    ///     its value is the per-class baseline field snapshot consumed by ENTERPVS in
    ///     <see cref="ProcessPacketEntitiesCore" />.
    ///     <para>
    ///         <b>Wire format</b> — faithful port of demofile-net's <c>StringTable.ReadUpdate</c>
    ///         (the earlier hand-rolled decode mis-modelled three sub-fields — the non-consecutive
    ///         index delta, the substring key history, and the value-size header — and silently
    ///         produced zero baselines, so no entity ever received baseline-only fields such as
    ///         <c>m_iMaxHealth</c> or a never-re-sent <c>m_iTeamNum</c>). Per entry:
    ///         <list type="number">
    ///             <item>
    ///                 index: always <c>+1</c>; if the leading bit is clear, additionally
    ///                 <c>+ ReadUVarInt32() + 1</c>.
    ///             </item>
    ///             <item>
    ///                 key: present-bit; if present, a history-bit selects between a raw UTF-8
    ///                 string and a (position,length) reference into the last-32 key history plus a
    ///                 UTF-8 suffix. If absent, the entry edits the existing entry at this index.
    ///             </item>
    ///             <item>
    ///                 value: present-bit; size from table metadata, or (variable tables) an
    ///                 optional per-entry Snappy flag then a varint / 17-bit byte count.
    ///             </item>
    ///         </list>
    ///     </para>
    /// </summary>
    private void ReadInstanceBaselineUpdate(byte[] data, int entries)
    {
        BitBuffer buf = new(data);

        try
        {
            string[] historyKeys = new string[entries > 0 ? entries : 1];
            int index = -1;

            for (int i = 0; i < entries; i++)
            {
                string key = "";
                byte[] value = [];

                // Entry index: +1, or +1 + varint delta when the "consecutive" bit is clear.
                index += 1;
                if (!buf.ReadOneBit())
                {
                    index += (int)buf.ReadUVarInt32() + 1;
                }

                // Key (optional; substring-compressed against the last-32 key history).
                if (buf.ReadOneBit())
                {
                    if (buf.ReadOneBit())
                    {
                        int position = (int)buf.ReadUBits(5);
                        int length = (int)buf.ReadUBits(5);
                        int historyIndex = i < 32 ? position : i - (32 - position);
                        string historical = historyIndex >= 0 && historyIndex < historyKeys.Length
                            ? historyKeys[historyIndex] ?? ""
                            : "";
                        key = length > historical.Length
                            ? historical + buf.ReadStringUtf8()
                            : historical[..length] + buf.ReadStringUtf8();
                    }
                    else
                    {
                        key = buf.ReadStringUtf8();
                    }
                }
                else if (index >= 0 && index < _ibEntries.Count)
                {
                    // Missing key → this is an edit to an existing entry; reuse its key.
                    key = _ibEntries[index].Key;
                }

                historyKeys[i] = key;

                // Value (optional). Fixed-size tables carry the size in metadata; variable tables
                // encode it per entry, optionally Snappy-compressed.
                if (buf.ReadOneBit())
                {
                    int bits = _ibUserDataSizeBits;
                    bool isCompressed = false;
                    if (!_ibUserDataFixedSize)
                    {
                        if ((_ibFlags & 0x1) != 0)
                        {
                            isCompressed = buf.ReadOneBit();
                        }

                        bits = _ibUsingVarintBitcounts
                            ? (int)(buf.ReadUBitVar() * 8)
                            : (int)(buf.ReadUBits(17) * 8);
                    }

                    value = new byte[(bits + 7) / 8];
                    buf.ReadBitsAsBytes(value, bits);
                    if (isCompressed)
                    {
                        value = Snappy.DecompressToArray(value);
                    }
                }

                // Persist into the entries list (append for a new index, overwrite for an edit).
                KeyValuePair<string, byte[]> entry = new(key, value);
                if (index == _ibEntries.Count)
                {
                    _ibEntries.Add(entry);
                }
                else if (index >= 0 && index < _ibEntries.Count)
                {
                    _ibEntries[index] = entry;
                }

                // Map classId → baseline bytes. Key is "classId" or "classId:altBaseline";
                // alternate baselines aren't requested by our ENTERPVS path (Baseline 0 only).
                if (key.Length > 0 && value.Length > 0 && int.TryParse(key.Split(':')[0], out int classId))
                {
                    _instanceBaselines[classId] = value;
                }
            }
        }
        catch
        {
            // A malformed update is non-fatal: entities fall back to delta-only population
            // (the pre-fix behaviour), so a parse slip degrades rather than crashes the replay.
        }
    }

    private void ProcessNetMessage(NetMessage msg)
    {
        switch (msg.Payload)
        {
            // DEM_SendTables → embedded CSVCMsg_FlattenedSerializer (size-prefixed)
            case CDemoSendTables sendTables:
                ProcessSendTables(sendTables);
                break;

            // DEM_ClassInfo → entity class registry (demo-layer, typically before DEM_FullPacket)
            case CDemoClassInfo classInfo:
                ProcessClassInfo(classInfo);
                break;

            // svc_ClassInfo → same registry, sent in signon/full-packet net messages.
            // Like CDemoClassInfo, this path must NOT overwrite _serverClassBits —
            // see the explanation in ProcessClassInfo. CSVCMsg_ServerInfo's
            // MaxClasses is the authoritative source for the bit width.
            case CSVCMsg_ClassInfo svcClassInfo:
                foreach (CSVCMsg_ClassInfo.Types.class_t cls in svcClassInfo.Classes)
                {
                    if (!string.IsNullOrEmpty(cls.ClassName))
                    {
                        _classIdToName[cls.ClassId] = cls.ClassName;
                    }
                }

                break;

            case CSVCMsg_ServerInfo serverInfo:
                _serverClassBits = (int)Math.Log2(serverInfo.MaxClasses) + 1;
                break;

            case CSVCMsg_FlattenedSerializer flatSer:
                // SVC variant: direct proto (no size prefix)
                Schema ??= RuntimeSchema.Parse(flatSer);
                break;

            case CSVCMsg_CreateStringTable createTable:
                // Tables are assigned ids in creation order (demofile-net keys
                // CSVCMsg_UpdateStringTable.TableId into this same sequence). Count every
                // create so the instancebaseline table's id is whatever it actually got,
                // rather than a hard-coded guess.
                int tableId = _stringTableCreateCount++;
                if (createTable.Name == "instancebaseline")
                {
                    _ibTableId = tableId;
                    _ibInitialized = true;
                    _ibUserDataFixedSize = createTable.UserDataFixedSize;
                    _ibUserDataSizeBits = createTable.UserDataSizeBits;
                    _ibUsingVarintBitcounts = createTable.UsingVarintBitcounts;
                    _ibFlags = createTable.Flags;
                    byte[] createData = createTable.DataCompressed
                        ? Snappy.DecompressToArray(createTable.StringData.Span)
                        : createTable.StringData.ToByteArray();
                    ReadInstanceBaselineUpdate(createData, createTable.NumEntries);
                }

                break;

            case CSVCMsg_UpdateStringTable updateTable:
                if (_ibInitialized && updateTable.TableId == _ibTableId && !updateTable.StringData.IsEmpty)
                {
                    ReadInstanceBaselineUpdate(updateTable.StringData.ToByteArray(), updateTable.NumChangedEntries);
                }

                break;

            case CSVCMsg_PacketEntities packetEntities:
                ProcessPacketEntities(packetEntities);
                break;
        }
    }

    // ── svc_PacketEntities ────────────────────────────────────────────────────

    private void ProcessPacketEntities(CSVCMsg_PacketEntities msg)
    {
        if (Schema is null || _classIdToName.Count == 0)
        {
            return;
        }

        if (msg.EntityData.IsEmpty)
        {
            return;
        }

        PacketCount++;

        WarnIfWrappersRegisteredWithoutLens();

        // Decode trace: arm the per-packet buffer ONLY when tracing is explicitly opted in
        // (Tracing.Enabled / CS2DEMOKIT_TRACE_DECODE). When off, _traceContextActive stays false,
        // every gated construction site below is skipped, and the ~7 M-per-load DecodeTraceEntry
        // constructs + List.Adds never happen — the healthy path pays one predicted branch.
        // Cleared each packet (when armed) so a long trace run keeps memory flat; the dump runs
        // only when an exception fires below AND the trace was armed.
        if (Tracing.Enabled)
        {
            _trace.Clear();
            _traceContextActive = true;
        }

        // Kept UNCONDITIONAL: these feed the error breadcrumb + the App-only DecodeError event on
        // EVERY run (the className lookup at the catch reads _curEntityIndex), so they must reset
        // regardless of the trace flag.
        _curEntityIndex = -1;
        _curUpdateKind = "";

        // For the UI debugger event: snapshot delta-unknown count before so we can report
        // the per-packet delta; capture whether this packet just NEWLY raised an error.
        int deltaUnknownBefore = DeltaUnknownCount;
        bool errorPresentBefore = LastEntityError is not null;

        try
        {
            ProcessPacketEntitiesCore(msg);
        }
        catch (Exception ex)
        {
            // A corrupt / schema-mismatched packet is non-fatal; log and skip.
            // Capture the full ToString (with stack trace) so the failure point is identifiable.
            LastEntityError = ex.ToString();
            if (!_errorLogged)
            {
                _errorLogged = true;
                ReportFirstDecodeError(ex, msg.EntityData.Length * 8);
            }

            // Surface the failure on the decode-error stream (the app's Output panel).
            // ClassName / Path are best-effort from the in-flight entity + last trace entry.
            if (DecodeErrorRaised is not null)
            {
                string className = _curEntityIndex >= 0 && CurrentEntities[_curEntityIndex] is { } live
                    ? live.ClassName
                    : "";
                string? lastPath = _trace.Count > 0 ? _trace[^1].Path : null;
                if (string.IsNullOrEmpty(lastPath))
                {
                    lastPath = null;
                }

                DecodeErrorRaised.Invoke(new DecodeError(
                    PacketCount,
                    CurrentFrameIndex,
                    _curEntityIndex,
                    className,
                    ex.Message,
                    lastPath));
            }
        }
        finally
        {
            _traceContextActive = false;
        }

        // Fire the debugger hook even when the packet succeeded — callers want to see
        // counters tick, not just halt on errors. hasNewDecodeError is true only on the
        // exact packet where an error first appeared.
        bool hasNewDecodeError = !errorPresentBefore && LastEntityError is not null;
        int deltaUnknownDelta = DeltaUnknownCount - deltaUnknownBefore;
        PacketProcessed?.Invoke(PacketCount, hasNewDecodeError, deltaUnknownDelta);
    }

    private void ProcessPacketEntitiesCore(CSVCMsg_PacketEntities msg)
    {
        BitBuffer entityBuf = new(msg.EntityData.ToByteArray());
        int entityIndex = -1;
        bool prof = Profiling.Enabled;
        long peStart = 0, peAlloc = 0;
        if (prof)
        {
            _profiled = true;
            _profPacketEntitiesCount++;
            peStart = Stopwatch.GetTimestamp();
            peAlloc = GC.GetAllocatedBytesForCurrentThread();
        }

        for (int i = 0; i < msg.UpdatedEntries; i++)
        {
            int preludeStart = entityBuf.TellBits;

            // Entity index: UBitVar delta from last index
            entityIndex += 1 + (int)entityBuf.ReadUBitVar();
            _curEntityIndex = entityIndex;

            // Guard against corrupted / mis-aligned stream (schema version mismatch etc.)
            // Cast to uint so a negative entityIndex also triggers the break.
            if ((uint)entityIndex >= MaxEdicts)
            {
                break;
            }

            // Update type flags (2 bits)
            uint updateFlags = entityBuf.ReadUBits(2);
            bool leavePvs = (updateFlags & 0b01) != 0;
            bool enterPvs = (updateFlags & 0b10) != 0;

            if (leavePvs)
            {
                // FHDR_LEAVEPVS — entity leaving PVS
                bool destroy = (updateFlags & 0b11) == 0b11; // FHDR_DELETE
                if (destroy)
                {
                    CurrentEntities.Remove(entityIndex);
                }
                else if (CurrentEntities[entityIndex] is { } dormant)
                {
                    dormant.IsInPvs = false;
                }

                if (_traceContextActive)
                {
                    AddTrace(new DecodeTraceEntry(
                        TraceKind.Prelude, PacketCount, entityIndex, destroy ? "LeaveDestroy" : "LeavePvs", "",
                        0, "", preludeStart, entityBuf.TellBits - preludeStart,
                        "", null, null, 0, 0));
                }

                continue;
            }

            if (enterPvs)
            {
                // FHDR_ENTERPVS — new entity entering PVS
                uint classId = entityBuf.ReadUBits(_serverClassBits > 0 ? _serverClassBits : 10);
                uint serialNum = entityBuf.ReadUBits(NumSerialNumberBits);

                // Skip the "maybe spawngroup handle" unknown field
                _ = entityBuf.ReadUVarInt32();

                if (_traceContextActive)
                {
                    AddTrace(new DecodeTraceEntry(
                        TraceKind.Prelude, PacketCount, entityIndex, "EnterPrelude", $"classId={classId} serial={serialNum} classBits={_serverClassBits}",
                        0, "", preludeStart, entityBuf.TellBits - preludeStart,
                        "", null, null, 0, 0));
                }

                if (!_classIdToName.TryGetValue((int)classId, out string? className))
                {
                    className = $"UnknownClass({classId})";
                }

                // Mirror demofile-net's create-vs-update semantics: ENTERPVS for an entity slot
                // already holding the same (className, serialNum) is an UPDATE, not a CREATE.
                // Without this, every full-update packet triggers EntityCreated for every entity
                // it re-sends — inflating phantom CCSGameRulesProxy creations from 1 to 1000+.
                EntityState? prev = CurrentEntities[entityIndex];
                bool isGenuinelyNew = prev is null
                                      || prev.ClassName != className
                                      || prev.Serial != (int)serialNum;
                EntityState state = CurrentEntities.GetOrCreate(entityIndex, className, (int)serialNum);
                state.IsInPvs = true;
                if (isGenuinelyNew)
                {
                    EntityCreated?.Invoke(entityIndex, state);
                }
                else
                {
                    EntityUpdated?.Invoke(entityIndex, state);
                }

                // Apply instance baseline if available
                if (_instanceBaselines.TryGetValue((int)classId, out byte[]? baseline))
                {
                    BitBuffer baselineBuf = new(baseline);
                    _curUpdateKind = "Baseline";
                    ReadEntityFields(ref baselineBuf, state, _fieldPathScratch);
                }

                _curUpdateKind = "Enter";
                ReadEntityFields(ref entityBuf, state, _fieldPathScratch);
            }
            else
            {
                // Delta update on existing entity.
                //
                // HasPvsVisBitsDeprecated: when this proto field is set, every delta entity has a
                // 2-bit prefix (deltaCmd). Bit 0 == 1 means "skip this entity" — the field consumed
                // the 2 bits but no further data follows. demofile-net's DemoParser.Entities.cs:356
                // has the canonical implementation. Without this, missing deltas cascade into
                // bit-misalignment that misreads classIds in subsequent ENTERPVS frames.
                if (msg.HasPvsVisBitsDeprecated > 0)
                {
                    uint deltaCmd = entityBuf.ReadUBits(2);
                    if ((deltaCmd & 0x1) == 1)
                    {
                        if (_traceContextActive)
                        {
                            AddTrace(new DecodeTraceEntry(
                                TraceKind.Prelude, PacketCount, entityIndex, "DeltaSkipped", "",
                                0, "", preludeStart, entityBuf.TellBits - preludeStart,
                                "", null, null, 0, 0));
                        }

                        continue;
                    }
                }

                EntityState? state = CurrentEntities[entityIndex];
                if (state is null)
                {
                    DeltaUnknownCount++;
                    if (_traceContextActive)
                    {
                        AddTrace(new DecodeTraceEntry(
                            TraceKind.Prelude, PacketCount, entityIndex, "DeltaUnknownEntity", "",
                            0, "", preludeStart, entityBuf.TellBits - preludeStart,
                            "", null, null, 0, 0));
                    }

                    continue; // delta on unknown entity — skip (see the bit-misalignment notes in KNOWN-AND-SUSPECTED-ISSUES.md)
                }

                if (_traceContextActive)
                {
                    AddTrace(new DecodeTraceEntry(
                        TraceKind.Prelude, PacketCount, entityIndex, "DeltaPrelude", state.ClassName,
                        0, "", preludeStart, entityBuf.TellBits - preludeStart,
                        "", null, null, 0, 0));
                }

                _curUpdateKind = "Delta";
                ReadEntityFields(ref entityBuf, state, _fieldPathScratch);
                EntityUpdated?.Invoke(entityIndex, state);
            }
        }

        if (prof)
        {
            _profPacketEntitiesTicks += Stopwatch.GetTimestamp() - peStart;
            _profPacketEntitiesAlloc += GC.GetAllocatedBytesForCurrentThread() - peAlloc;
        }
    }

    // ── DEM_SendTables ────────────────────────────────────────────────────────

    private void ProcessSendTables(CDemoSendTables msg)
    {
        if (msg.Data.IsEmpty)
        {
            return;
        }

        // CDemoSendTables.data = [uvarint size][CSVCMsg_FlattenedSerializer bytes]
        BitBuffer buf = new(msg.Data.ToByteArray());
        int size = (int)buf.ReadUVarInt32();
        byte[] raw = buf.ReadBytes(size);

        CSVCMsg_FlattenedSerializer? flatSer = CSVCMsg_FlattenedSerializer.Parser.ParseFrom(raw);
        Schema ??= RuntimeSchema.Parse(flatSer);
    }

    /// <summary>
    ///     Invokes a leaf descriptor's decoder while recording bit consumption + metadata
    ///     into the trace ring buffer (when a packet trace context is active).
    /// </summary>
    private void ReadAndTrace(ref BitBuffer buf, EntityState state, FieldDescriptor desc)
    {
        int before = buf.TellBits;
        try
        {
            // The lane-indexed write site, extended to handle Lens-driven
            // lane drift (the descriptor's natural decoder lane differs from the
            // Lens-declared target lane). Each branch dispatches on the descriptor's
            // SlotAddr.Lane (resolved at BuildFieldDescs time) and writes directly to the
            // bound EntityState lane — no string hash on the hot path. Fallback is taken
            // for array elements and any unmapped leaf (e.g. when the shape was built
            // without the builder, or a path falls outside the bound shape).
            switch (desc.Kind)
            {
                case DecoderKind.Int when desc.IntDecoder is { } id:
                    int iv = id(ref buf);
                    if (_suppressFieldStore)
                    {
                        break; // decode-and-discard: bits consumed, value not stored
                    }

                    switch (desc.SlotAddr.Lane)
                    {
                        case LaneKind.Int:
                            state.SetIntSlot(desc.SlotAddr.Slot, iv);
                            break;
                        case LaneKind.Float:
                            // Lens drift: int wire, float Lens lane. Coerce.
                            state.SetFloatSlot(desc.SlotAddr.Slot, iv);
                            break;
                        case LaneKind.Object:
                            // Lens drift: int wire, object Lens lane (e.g. CastToUInt64).
                            object boxed = desc.Transform == LensTransform.CastToUInt64
                                ? (ulong)iv
                                : Boxes.Int(iv);
                            state.SetObjectSlot(desc.SlotAddr.Slot, boxed);
                            break;
                        default:
                            state.SetFallback(desc.Path, Boxes.Int(iv));
                            break;
                    }

                    break;
                case DecoderKind.Float when desc.FloatDecoder is { } fd:
                    float fv = fd(ref buf);
                    if (_suppressFieldStore)
                    {
                        break; // decode-and-discard
                    }

                    switch (desc.SlotAddr.Lane)
                    {
                        case LaneKind.Float:
                            state.SetFloatSlot(desc.SlotAddr.Slot, fv);
                            break;
                        case LaneKind.Int:
                            // Lens drift: float wire, int Lens lane. Coerce.
                            state.SetIntSlot(desc.SlotAddr.Slot, (int)fv);
                            break;
                        case LaneKind.Object:
                            state.SetObjectSlot(desc.SlotAddr.Slot, fv);
                            break;
                        default:
                            state.SetFallback(desc.Path, fv);
                            break;
                    }

                    break;
                default:
                    if (desc.Decoder is not null)
                    {
                        object? ov = desc.Decoder(ref buf);
                        if (_suppressFieldStore)
                        {
                            break; // decode-and-discard
                        }

                        switch (desc.SlotAddr.Lane)
                        {
                            case LaneKind.Object:
                                state.SetObjectSlot(desc.SlotAddr.Slot, ov);
                                break;
                            case LaneKind.Int:
                                // Lens drift: object wire (uint64/etc.), int Lens lane.
                                // Common case: HandleIndex (UInt64Raw decoder → int lane).
                                // Truncating to int is lossless for the handle bits the
                                // wrapper's mask uses (low 14 bits) and the serial in
                                // bits 16-31 — all live in the low 32 bits of the wire.
                                state.SetIntSlot(desc.SlotAddr.Slot, CoerceToInt(ov));
                                break;
                            case LaneKind.Float:
                                state.SetFloatSlot(desc.SlotAddr.Slot, CoerceToFloat(ov));
                                break;
                            default:
                                state.SetFallback(desc.Path, ov);
                                break;
                        }
                    }

                    break;
            }
        }
        finally
        {
            if (_traceContextActive)
            {
                RuntimeField? f = desc.Field;
                AddTrace(new DecodeTraceEntry(
                    TraceKind.FieldRead, PacketCount, _curEntityIndex, _curUpdateKind, state.ClassName,
                    0, "", before, buf.TellBits - before,
                    desc.Path,
                    f?.TypeName, f?.Encoder,
                    f?.BitCount ?? 0, f?.EncodeFlags ?? 0));
            }
        }
    }

    /// <summary>
    ///     Reads all changed fields from the entity bit stream into <paramref name="state" />.
    ///     This implements the Huffman-coded field-path + per-field decoder loop.
    /// </summary>
    private void ReadEntityFields(ref BitBuffer buf, EntityState state, List<FieldPath> pathScratch)
    {
        if (Schema is null)
        {
            return;
        }

        List<FieldDescriptor>? descs = GetFieldDescriptors(state.ClassName);
        if (descs is null)
        {
            return;
        }

        // Storage allowlist: decode-and-discard for classes not in the filter (bits still consumed).
        _suppressFieldStore = StoreClassFilter is not null && !StoreClassFilter.Contains(state.ClassName);

        // Lazy-bind the per-class shape so every state that flows through this method
        // (live entities, EnterPvs initial fill, baselines, PeekEntityUpdates temps) gets
        // its lanes allocated before the first SetIntSlot / SetFloatSlot / SetObjectSlot
        // call. Idempotent for the same shape reference; EntityState.BindShape skips
        // re-allocation when called twice with the same instance.
        if (!_suppressFieldStore && state.Shape is null && _classShapes.TryGetValue(state.ClassName, out ClassShape? shape))
        {
            state.BindShape(shape);
        }

        if (_traceContextActive)
        {
            AddTrace(new DecodeTraceEntry(
                TraceKind.BeginEntity, PacketCount, _curEntityIndex, _curUpdateKind, state.ClassName,
                0, "", buf.TellBits, 0,
                "", null, null, 0, 0));
        }

        bool prof = Profiling.Enabled;
        long fpStart = 0, fpAlloc = 0, fvStart = 0, fvAlloc = 0;
        if (prof)
        {
            _profiled = true;
            _profEntityFieldReads++;
            fpStart = Stopwatch.GetTimestamp();
            fpAlloc = GC.GetAllocatedBytesForCurrentThread();
        }

        // Collect all field paths first, then decode (mirrors demofile-net). The caller supplies the
        // path list as a scratch buffer so the hot Replay path can REUSE one per-tracker buffer
        // (cleared, not re-allocated) across all ~3.47M field reads per load instead of allocating a
        // List+array each call. ReadEntityFields is single-threaded per tracker and never re-entrant
        // (the baseline read at the ENTERPVS site returns before the enter/delta read; nested fields go
        // through ReadAndTrace, not back here), so the Replay buffer is safe to share across calls; the
        // read-only PeekEntityUpdates path passes its OWN buffer so a peek can't disturb Replay's.
        List<FieldPath> paths = pathScratch;
        paths.Clear();
        FieldPath fp = FieldPath.Default;

        // CS2 entities have at most ~300 top-level fields; array expansion adds at most ~64 per array.
        // 2048 is far above any real entity while still catching runaway mis-aligned decodes quickly.
        const int MaxFieldPaths = 2_048;
        for (int pathCount = 0; pathCount < MaxFieldPaths; pathCount++)
        {
            int opBefore = buf.TellBits;
            FieldPathEncodingOp op = FieldPathEncoding.ReadOp(ref buf);
            if (op.Reader is null)
            {
                if (_traceContextActive)
                {
                    AddTrace(new DecodeTraceEntry(
                        TraceKind.PathOp, PacketCount, _curEntityIndex, _curUpdateKind, state.ClassName,
                        pathCount, op.Name, opBefore, buf.TellBits - opBefore,
                        fp, null, null, 0, 0));
                }

                break;
            }

            try
            {
                op.Reader(ref buf, ref fp);
            }
            catch (Exception ex)
            {
                // Capture the failed op into the trace before re-throwing so the dump shows
                // it in chronological order.
                if (_traceContextActive)
                {
                    AddTrace(new DecodeTraceEntry(
                        TraceKind.PathOp, PacketCount, _curEntityIndex, _curUpdateKind, state.ClassName,
                        pathCount, op.Name, opBefore, buf.TellBits - opBefore,
                        "<threw>", null, null, 0, 0));
                }

                // Re-throw with entity / op / path context so the catch handler in
                // ProcessPacketEntities surfaces something actionable instead of
                // a bare "FieldPath is full".
                throw new InvalidDataException(
                    $"Failed to apply field-path op '{op.Name}' on entity '{state.ClassName}' " +
                    $"after {pathCount} ops; current path={fp}", ex);
            }

            if (_traceContextActive)
            {
                AddTrace(new DecodeTraceEntry(
                    TraceKind.PathOp, PacketCount, _curEntityIndex, _curUpdateKind, state.ClassName,
                    pathCount, op.Name, opBefore, buf.TellBits - opBefore,
                    fp, null, null, 0, 0));
            }

            paths.Add(fp);
        }

        if (prof)
        {
            fvStart = Stopwatch.GetTimestamp();
            fvAlloc = GC.GetAllocatedBytesForCurrentThread();
            _profFieldPathTicks += fvStart - fpStart;
            _profFieldPathAlloc += fvAlloc - fpAlloc;
        }

        // Decode each field path
        foreach (FieldPath path in paths)
        {
            ReadOnlySpan<int> span = path.AsSpan();
            int topLevel = span.Length > 0 ? span[0] : 0;

            // Path values can be negative (e.g. NonTopoComplexPack4Bits subtracts 7).
            if ((uint)topLevel >= (uint)descs.Count)
            {
                continue; // field index out of range or negative — skip
            }

            FieldDescriptor desc = descs[topLevel];

            if (span.Length == 1)
            {
                ReadAndTrace(ref buf, state, desc);
            }
            else
            {
                // Nested path — walk child descriptors
                ResolveNestedField(ref buf, state, desc, span[1..]);
            }
        }

        if (prof)
        {
            _profFieldValueTicks += Stopwatch.GetTimestamp() - fvStart;
            _profFieldValueAlloc += GC.GetAllocatedBytesForCurrentThread() - fvAlloc;
        }
    }

    private void ResolveNestedField(ref BitBuffer buf, EntityState state, FieldDescriptor parent, ReadOnlySpan<int> remaining)
    {
        if (parent.ChildDescs is null || parent.ChildDescs.Count == 0 || remaining.IsEmpty)
        {
            return;
        }

        int idx = remaining[0];
        // Atomic-element vectors (NetworkedVector<byte>, etc.) can carry indices past our pregen
        // size — m_VoxelFrameData on CSmokeGrenadeProjectile observed at idx 1024+. All elements
        // in such arrays are decoder-equivalent (same wire shape; only path string differs), so
        // out-of-range indices reuse the last pregen entry. The bits consume correctly; the state
        // bucket gets clobbered for very high indices, which is acceptable for opaque payload
        // arrays (voxel bytes, predicted variable buffers) that nothing queries by index.
        if (idx < 0)
        {
            return;
        }

        if (idx >= parent.ChildDescs.Count)
        {
            idx = parent.ChildDescs.Count - 1;
        }

        FieldDescriptor child = parent.ChildDescs[idx];

        if (remaining.Length == 1)
        {
            ReadAndTrace(ref buf, state, child);
        }
        else
        {
            ResolveNestedField(ref buf, state, child, remaining[1..]);
        }
    }

    // ── Decode-error stream ───────────────────────────────────────────────────

    /// <summary>
    ///     One packet-level entity decode failure. Surfaced for the app Output panel's
    ///     "Decode errors" channel. <see cref="EntityIndex" /> is the entity slot in flight when
    ///     the decode threw (-1 if unknown); <see cref="Path" /> is the last field path attempted
    ///     (null when the failure was outside a field-read).
    /// </summary>
    public readonly record struct DecodeError(
        int PacketIndex,
        int FrameIndex,
        int EntityIndex,
        string ClassName,
        string Message,
        string? Path);

    // ── Decode trace types + dumper ───────────────────────────────────────────

    private enum TraceKind : byte
    {
        BeginEntity,
        PathOp,
        FieldRead,
        NestedDescent,
        Prelude
    }

    /// <summary>Captures one trace event during entity-data decoding.</summary>
    private readonly struct DecodeTraceEntry(
        TraceKind kind,
        int packet,
        int entity,
        string updateKind,
        string className,
        int opIndex,
        string opName,
        int bitPosBefore,
        int bitsConsumed,
        string path,
        string? typeName,
        string? encoder,
        int bitCount,
        int encodeFlags)
    {
        /// <summary>Kind.</summary>
        public TraceKind Kind { get; } = kind;

        /// <summary>Packet.</summary>
        public int Packet { get; } = packet;

        /// <summary>Entity.</summary>
        public int Entity { get; } = entity;

        /// <summary>Update kind.</summary>
        public string UpdateKind { get; } = updateKind;

        /// <summary>Class name.</summary>
        public string ClassName { get; } = className;

        /// <summary>Op index.</summary>
        public int OpIndex { get; } = opIndex;

        /// <summary>Op name.</summary>
        public string OpName { get; } = opName;

        /// <summary>Bit pos before.</summary>
        public int BitPosBefore { get; } = bitPosBefore;

        /// <summary>Bits consumed.</summary>
        public int BitsConsumed { get; } = bitsConsumed;

        // Path storage: either a precomputed string (most call sites pass "" or an existing
        // descriptor path) or a captured FieldPath whose string form is DEFERRED to dump-time. The
        // path-collection loop builds millions of PathOp entries per load; storing the 32-byte
        // FieldPath struct by value and calling ToString() only when DumpTrace / the DecodeError event
        // reads Path (i.e. on a decode error — never on the healthy path) removes the per-op
        // fp.ToString() string allocation that dominated the remaining field-path decode garbage.
        private readonly string? _pathString = path;
        private readonly FieldPath _pathValue = default;
        private readonly bool _hasPathValue = false;

        /// <summary>Path (lazily materialised from the captured FieldPath when built from one).</summary>
        public string Path => _hasPathValue ? _pathValue.ToString() : _pathString ?? "";

        /// <summary>Type name.</summary>
        public string? TypeName { get; } = typeName;

        /// <summary>Encoder.</summary>
        public string? Encoder { get; } = encoder;

        /// <summary>Bit count.</summary>
        public int BitCount { get; } = bitCount;

        /// <summary>Encode flags.</summary>
        public int EncodeFlags { get; } = encodeFlags;

        /// <summary>
        ///     FieldPath overload used by the hot path-collection sites. Stores the FieldPath struct
        ///     (captured by value) and defers <see cref="FieldPath.ToString" /> to <see cref="Path" />
        ///     access at dump-time. Chains to the primary constructor with an empty string path.
        /// </summary>
        public DecodeTraceEntry(
            TraceKind kind,
            int packet,
            int entity,
            string updateKind,
            string className,
            int opIndex,
            string opName,
            int bitPosBefore,
            int bitsConsumed,
            FieldPath pathValue,
            string? typeName,
            string? encoder,
            int bitCount,
            int encodeFlags)
            : this(kind, packet, entity, updateKind, className, opIndex, opName, bitPosBefore,
                bitsConsumed, "", typeName, encoder, bitCount, encodeFlags)
        {
            _pathValue = pathValue;
            _hasPathValue = true;
        }
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    private enum DecoderKind
    {
        Object,
        Int,
        Float
    }

    /// <summary>
    ///     Array-element descriptors, materialised on first access instead of all at once.
    ///     <para>
    ///         Every array field used to pre-generate <see cref="ArrayPregenSize" /> (1024) descriptors —
    ///         and for arrays-of-class, a full recursive child tree PER ELEMENT. On a real demo that came
    ///         to 3,117,491 FieldDescriptor objects / 231 MB, the single largest consumer of the loaded
    ///         heap, plus ~1024 interpolated <c>"path[N]"</c> strings per array field. Actual demos touch
    ///         a handful of indices per array, so nearly all of it was never read.
    ///     </para>
    ///     <para>
    ///         This is safe precisely because of the invariant <see cref="ResolveNestedField" /> already
    ///         relies on: array elements are decoder-equivalent — same wire shape, only the path string
    ///         differs — which is why that method can clamp an out-of-range index onto the last entry.
    ///         Deferring construction changes when a descriptor is built, never what it decodes.
    ///         <see cref="Count" /> reports the full logical size, so that clamp is unaffected.
    ///     </para>
    ///     <para>
    ///         Races are benign by the same invariant: two threads may both materialise index <c>i</c> and
    ///         one write wins, but the loser's instance is functionally identical and remains valid for
    ///         the caller holding it. No lock, to keep the decode path allocation- and contention-free.
    ///     </para>
    /// </summary>
    private sealed class LazyArrayElementDescs : IReadOnlyList<FieldDescriptor>
    {
        private readonly FieldDescriptor?[] _cache;
        private readonly Func<int, FieldDescriptor> _create;

        public LazyArrayElementDescs(int count, Func<int, FieldDescriptor> create)
        {
            _cache = new FieldDescriptor?[count];
            _create = create;
        }

        public FieldDescriptor this[int index] => _cache[index] ??= _create(index);

        public int Count => _cache.Length;

        // Enumerating materialises everything, defeating the point. Nothing on the decode path
        // enumerates element lists (they are index-addressed); FindLeafField has a direct-index fast
        // path for exactly this reason. Kept correct for debug/inspection callers.
        public IEnumerator<FieldDescriptor> GetEnumerator()
        {
            for (int i = 0; i < _cache.Length; i++)
            {
                yield return this[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class FieldDescriptor
    {
        /// <summary>Object-decoder constructor: stores the path, generic decoder, and any child descriptors.</summary>
        public FieldDescriptor(string path, FieldDecoder? decoder, IReadOnlyList<FieldDescriptor>? childDescs)
        {
            Path = path;
            Decoder = decoder;
            ChildDescs = childDescs;
            Kind = DecoderKind.Object;
        }

        /// <summary>Int-decoder constructor: stores the path, integer decoder, and any child descriptors.</summary>
        public FieldDescriptor(string path, IntDecoder intDecoder, IReadOnlyList<FieldDescriptor>? childDescs)
        {
            Path = path;
            IntDecoder = intDecoder;
            ChildDescs = childDescs;
            Kind = DecoderKind.Int;
        }

        /// <summary>Float-decoder constructor: stores the path, float decoder, and any child descriptors.</summary>
        public FieldDescriptor(string path, FloatDecoder floatDecoder, IReadOnlyList<FieldDescriptor>? childDescs)
        {
            Path = path;
            FloatDecoder = floatDecoder;
            ChildDescs = childDescs;
            Kind = DecoderKind.Float;
        }

        /// <summary>Child descs.</summary>
        public IReadOnlyList<FieldDescriptor>? ChildDescs { get; }

        /// <summary>Decoder.</summary>
        public FieldDecoder? Decoder { get; }

        // Schema metadata used by the decode trace. Populated by the BuildFieldDescs family
        // via object-initializer after construction (some descriptors are synthesised — array
        // elements, char[N] strings — and use a cloned RuntimeField; nullable lets fixed-array
        // container slots leave it blank since they have no decoder of their own).
        /// <summary>Field.</summary>
        public RuntimeField? Field { get; set; }

        /// <summary>Float decoder.</summary>
        public FloatDecoder? FloatDecoder { get; }

        /// <summary>Int decoder.</summary>
        public IntDecoder? IntDecoder { get; }

        /// <summary>Kind.</summary>
        public DecoderKind Kind { get; }

        /// <summary>Path.</summary>
        public string Path { get; }

        /// <summary>
        ///     The lane + slot index where this descriptor's decoded value lands on the
        ///     <see cref="EntityState" />. Populated for leaf descriptors on the non-array
        ///     spine by <see cref="BuildFieldDescs" />; defaults to <see cref="SlotAddr.Fallback" />
        ///     for array elements, fixed-array containers, and any descriptor built before
        ///     a shape was being threaded.
        /// </summary>
        public SlotAddr SlotAddr { get; set; } = SlotAddr.Fallback;

        /// <summary>
        ///     Schema Lens transform baked into the descriptor at bootstrap.
        ///     <see cref="LensTransform.None" /> when the Lens resolver wasn't bound or the
        ///     path was unmapped. Honoured by <see cref="ReadAndTrace" /> only for transforms
        ///     that mutate the stored lane value — <see cref="LensTransform.BoolFromInt" />
        ///     and <see cref="LensTransform.HandleIndex" /> are identity on the lane in V1.
        /// </summary>
        public LensTransform Transform { get; set; } = LensTransform.None;
    }
}
