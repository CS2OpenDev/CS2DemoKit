#region

using System.Collections;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Wraps an <see cref="EntityTracker" /> together with the demo's frame list to provide
///     incremental, forward-only tick-indexed entity state access.
///     <para>
///         Seeking is O(k) where k is the number of frames advanced — not O(n) from the start.
///         Each call to <see cref="SeekToTick" /> processes only the frames that lie between the
///         previous position and the requested tick.
///     </para>
///     <para>
///         <b>Thread safety:</b> not thread-safe. Each parallel rule branch should call
///         <see cref="IDemoContext.CreateEntityLayer" /> to obtain its own private instance.
///     </para>
///     <para>
///         <b>Forward-only:</b> seeking backwards is a no-op. Use <see cref="Reset" /> to start
///         from tick 0.
///     </para>
/// </summary>
/// <remarks>
///     Creates a new layer over <paramref name="frames" />, positioned at tick 0.
/// </remarks>
public sealed class EntityStateLayer(IReadOnlyList<DemoFrame> frames)
{
    private int _nextFrameIndex;

    /// <summary>Tick of the most recently processed frame (0 before any seek).</summary>
    public int CurrentTick => Tracker.CurrentTick;

    /// <summary>Current entity state, reflecting all frames processed so far.</summary>
    public EntityTracker Tracker { get; private set; } = BootstrapTracker();

    /// <summary>
    ///     Resets the layer to tick 0 by discarding the current <see cref="Tracker" /> and
    ///     creating a fresh one. After this call, <see cref="SeekToTick" /> will replay from
    ///     the beginning of the demo.
    /// </summary>
    public void Reset()
    {
        Tracker = BootstrapTracker();
        _nextFrameIndex = 0;
    }

    /// <summary>
    ///     Constructs a fresh <see cref="EntityTracker" /> with the Schema Lens resolver
    ///     bound (the lane-mapping step whose omission silently degrades typed reads).
    ///     Typed wrappers are the SDK-emitted set since the SDK cutover: providers bind
    ///     them per entity via <c>SdkEntityWorlds.Wrap</c>, which also registers the SDK
    ///     factories on this tracker on first use.
    /// </summary>
    private static EntityTracker BootstrapTracker() => EntityTrackerFactory.CreateCurated();

    /// <summary>
    ///     Advances the entity state to include all frames with
    ///     <c>tick &lt;= <paramref name="targetTick" /></c>.
    ///     If <paramref name="targetTick" /> is before or equal to <see cref="CurrentTick" />,
    ///     this is a no-op and the current <see cref="Tracker" /> is returned unchanged.
    /// </summary>
    /// <returns>
    ///     The <see cref="EntityTracker" /> at the requested tick (same instance as
    ///     <see cref="Tracker" />).
    /// </returns>
    public EntityTracker SeekToTick(int targetTick)
    {
        if (Tracker.CurrentTick >= targetTick)
        {
            return Tracker;
        }

        // Find the exclusive end index: all frames with tick <= targetTick.
        int end = _nextFrameIndex;
        while (end < frames.Count && frames[end].ServerTick <= targetTick)
        {
            end++;
        }

        if (end > _nextFrameIndex)
        {
            Tracker.Replay(new FrameSlice(frames, _nextFrameIndex, end - _nextFrameIndex));
            _nextFrameIndex = end;
        }

        return Tracker;
    }

    /// <summary>
    ///     Advances so every frame with index <c>&lt; <paramref name="frameIndex" /></c> is applied and the
    ///     current state is the PRE-frame state for <paramref name="frameIndex" /> — i.e. the entity state
    ///     just before that frame's packet-entities update. This is the frame-accurate analogue of the
    ///     scanner's pre-frame capture (which happens at the start of <c>AdvanceAndPoll</c>, before its
    ///     tick seek); unlike <see cref="SeekToTick" /> it is exact even when consecutive frames share a
    ///     <c>ServerTick</c>. Forward-only: a <paramref name="frameIndex" /> at or before the current
    ///     position is a no-op (use <see cref="Reset" /> to rewind).
    /// </summary>
    /// <returns>The <see cref="EntityTracker" /> positioned before <paramref name="frameIndex" />.</returns>
    public EntityTracker SeekBeforeFrame(int frameIndex)
    {
        int end = Math.Min(frameIndex, frames.Count); // exclusive: apply frames [_nextFrameIndex, frameIndex)
        if (end > _nextFrameIndex)
        {
            Tracker.Replay(new FrameSlice(frames, _nextFrameIndex, end - _nextFrameIndex));
            _nextFrameIndex = end;
        }

        return Tracker;
    }

    /// <summary>
    ///     Pre-positions this layer at a <c>DEM_FullPacket</c> checkpoint so a parallel chunk worker can
    ///     drive the SAME <see cref="SeekToTick" /> mechanism as the sequential scanner, just starting
    ///     from the checkpoint instead of from tick 0. Serves the parallel entity decode.
    ///     <para>
    ///         Mechanism: replay the schema prefix <c>[0, schemaPrefixEnd)</c> — the signon frames that
    ///         load SendTables / ClassInfo / the initial string tables — via the un-gated
    ///         <see cref="EntityTracker.Replay" /> (NOT <see cref="SeekToTick" />, whose tick gate would
    ///         skip the <c>tick == -1</c> signon frames), then drop the entities that prefix created
    ///         (<see cref="EntityTracker.ResetEntitiesKeepSchema" />), seed the per-class instancebaseline
    ///         table from the most recent full packet that carries it
    ///         (<see cref="EntityTracker.LoadInstanceBaselineSnapshot" /> — needed so entities CREATED after
    ///         the checkpoint decode with their baseline fields), and seed the full entity set from the
    ///         checkpoint's own snapshot (<see cref="EntityTracker.ProcessFullPacketCheckpoint" /> — its
    ///         bundled string-table snapshot + full <c>PacketEntities</c>). After this call the layer is
    ///         positioned exactly as a sequential layer would be just before
    ///         <c>checkpointFrameIndex + 1</c>: <see cref="CurrentTick" /> is the checkpoint's tick and the
    ///         next frame <see cref="SeekToTick" /> will apply is the one after the checkpoint.
    ///     </para>
    ///     <para>
    ///         <b>Invariant (asserted loudly):</b> the checkpoint frame must have no same-tick SUCCESSOR (a
    ///         later frame sharing its <c>ServerTick</c>). <see cref="SeekToTick" /> folds every frame with
    ///         <c>tick &lt;= target</c> into one advance, so a same-tick successor's delta would be present
    ///         in the sequential digest but absent from the bare checkpoint snapshot — a silent divergence.
    ///         CS2 emits the full packet AFTER that tick's delta packet (so the snapshot already includes
    ///         it) and the following frame is the next tick, so this holds on every observed demo; rather
    ///         than carry an unvalidated same-tick-replay branch we throw if a demo ever violates it.
    ///     </para>
    /// </summary>
    /// <param name="checkpointFrameIndex">Index of the <c>DEM_FullPacket</c> frame to start from.</param>
    /// <param name="schemaPrefixEnd">
    ///     Exclusive end of the schema-loading prefix (the first gameplay <c>DEM_Packet</c> index);
    ///     replayed in full to load the serializer before the entities are reset.
    /// </param>
    public void PrimeFromCheckpoint(int checkpointFrameIndex, int schemaPrefixEnd)
    {
        // 1. Load the schema (serializer / class info / initial string tables) without the tick gate.
        if (schemaPrefixEnd > 0)
        {
            Tracker.Replay(new FrameSlice(frames, 0, schemaPrefixEnd));
        }

        // 2. Drop the entities the prefix created; keep the schema.
        Tracker.ResetEntitiesKeepSchema();

        // 2b. Seed the instancebaseline table so entities CREATED after the checkpoint (a mid-chunk
        //     ENTERPVS) decode with their per-class baseline fields. The full-packet string-table dump is
        //     INCREMENTAL — a full packet carries the instancebaseline table only when it changed since the
        //     previous one (and when present it is COMPLETE), so walk back from the checkpoint to the most
        //     recent full packet that carries it (the table was unchanged in between). Without this, a
        //     baseline-sourced field such as a projectile's m_hThrower stays unset on mid-chunk creates.
        for (int i = checkpointFrameIndex; i >= 0; i--)
        {
            if (frames[i].Command == "DEM_FullPacket" && Tracker.LoadInstanceBaselineSnapshot(frames[i]))
            {
                break;
            }
        }

        // 3. Seed the full entity set from the checkpoint snapshot (sets CurrentTick to the checkpoint tick).
        DemoFrame checkpoint = frames[checkpointFrameIndex];
        Tracker.ProcessFullPacketCheckpoint(checkpoint);

        // 4. Enforce the no-same-tick-successor invariant (see remarks). Loud, never silently handled.
        int next = checkpointFrameIndex + 1;
        if (next < frames.Count && frames[next].ServerTick == checkpoint.ServerTick)
        {
            throw new InvalidOperationException(
                $"PrimeFromCheckpoint: full-packet frame {checkpointFrameIndex} (tick {checkpoint.ServerTick}) " +
                $"has a same-tick successor at frame {next}; the checkpoint snapshot would miss its delta. " +
                "The parallel chunked decode assumes full packets have no same-tick successor.");
        }

        // 5. The next frame the worker's SeekToTick should apply is the one after the checkpoint.
        _nextFrameIndex = next;
    }

    // ── Zero-allocation frame slice ───────────────────────────────────────────

    /// <summary>
    ///     A lightweight, zero-copy window over a contiguous sub-range of
    ///     <paramref name="source" /> passed to <see cref="EntityTracker.Replay" />.
    ///     No elements are copied — the wrapper is the only allocation.
    /// </summary>
    private sealed class FrameSlice(IReadOnlyList<DemoFrame> source, int start, int count)
        : IReadOnlyList<DemoFrame>
    {
        /// <inheritdoc />
        public int Count => count;

        /// <inheritdoc />
        public IEnumerator<DemoFrame> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                yield return source[start + i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc />
        public DemoFrame this[int index] => source[start + index];
    }
}
