#region

using System.Collections;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     One curated <see cref="EntityTracker" /> advanced forward. Built over a frame list it seeks
///     by tick or frame index, replaying only the frames between where it is and where it is asked
///     to be; built without one it is fed frames by its owner through <see cref="Apply" />, which
///     is what a forward reader drives. Backward seeking is a no-op either way; <see cref="Reset" />
///     rebuilds the tracker from nothing.
/// </summary>
public sealed class EntityStateLayer
{
    private readonly IReadOnlyList<DemoFrame>? _frames;
    private int _nextFrameIndex;

    /// <summary>A layer that seeks over <paramref name="frames" />.</summary>
    public EntityStateLayer(IReadOnlyList<DemoFrame> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        _frames = frames;
    }

    /// <summary>A layer with no frames of its own; its owner applies them.</summary>
    public EntityStateLayer()
    {
    }

    /// <summary>True when the layer can seek on its own.</summary>
    public bool HasFrames => _frames is not null;

    public int CurrentTick => Tracker.CurrentTick;

    public EntityTracker Tracker { get; private set; } = BootstrapTracker();

    private bool _storeUnlensedFields = true;
    private EntityStateLayer? _template;

    /// <summary>
    ///     Forwarded to <see cref="EntityTracker.StoreUnlensedFields" /> on the current tracker and
    ///     every one <see cref="Reset" /> builds. The analysis engine turns it off: its providers
    ///     read lensed lanes only, and the fallback store is a dictionary and a box per write.
    /// </summary>
    public bool StoreUnlensedFields
    {
        get => _storeUnlensedFields;
        set
        {
            _storeUnlensedFields = value;
            Tracker.StoreUnlensedFields = value;
        }
    }

    public void Reset()
    {
        Tracker = BootstrapTracker();
        Tracker.StoreUnlensedFields = _storeUnlensedFields;
        if (_template is not null)
        {
            Tracker.AdoptSchemaState(_template.Tracker);
        }

        _nextFrameIndex = 0;
    }

    /// <summary>
    ///     Takes the schema state of <paramref name="template" />, a layer that has applied the signon
    ///     prefix, instead of replaying that prefix: the schema is parsed once per demo and shared.
    ///     A prime after this skips the prefix. Survives <see cref="Reset" />.
    /// </summary>
    public void AdoptSchema(EntityStateLayer template)
    {
        ArgumentNullException.ThrowIfNull(template);
        _template = template;
        Tracker.AdoptSchemaState(template.Tracker);
    }

    private static EntityTracker BootstrapTracker() => EntityTrackerFactory.CreateCurated();

    /// <summary>Applies one frame. The caller guarantees recording order.</summary>
    public void Apply(DemoFrame frame) => Tracker.AdvanceOneFrame(frame);

    /// <summary>
    ///     Advances through every not-yet-applied frame whose tick is at most
    ///     <paramref name="targetTick" />. A target at or behind the current tick applies nothing.
    /// </summary>
    /// <exception cref="InvalidOperationException">The layer was built without frames.</exception>
    public EntityTracker SeekToTick(int targetTick)
    {
        IReadOnlyList<DemoFrame> frames = RequireFrames();
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

    /// <summary>Advances through every not-yet-applied frame before <paramref name="frameIndex" />.</summary>
    /// <exception cref="InvalidOperationException">The layer was built without frames.</exception>
    public EntityTracker SeekBeforeFrame(int frameIndex)
    {
        IReadOnlyList<DemoFrame> frames = RequireFrames();
        int end = Math.Min(frameIndex, frames.Count); // exclusive: apply frames [_nextFrameIndex, frameIndex)
        if (end > _nextFrameIndex)
        {
            Tracker.Replay(new FrameSlice(frames, _nextFrameIndex, end - _nextFrameIndex));
            _nextFrameIndex = end;
        }

        return Tracker;
    }

    /// <summary>
    ///     Seeds the layer from a <c>DEM_FullPacket</c> checkpoint: the retained signon prefix is
    ///     applied ungated unless a template's schema was adopted, the entities it created are
    ///     dropped, the most recent full packet before the checkpoint that carried the
    ///     <c>instancebaseline</c> table is loaded (the full-packet string-table dump is
    ///     incremental, so it may be an earlier one), then the checkpoint's own snapshot. What a
    ///     chunk worker primes with.
    /// </summary>
    /// <exception cref="InvalidOperationException">The checkpoint has a same-tick successor.</exception>
    public void PrimeFromCheckpoint(IReadOnlyList<DemoFrame> signonPrefix, DemoFrame? instanceBaselineFullPacket,
        DemoFrame checkpoint, DemoFrame? successor)
    {
        ArgumentNullException.ThrowIfNull(signonPrefix);
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (_template is null)
        {
            foreach (DemoFrame frame in signonPrefix)
            {
                Tracker.AdvanceOneFrame(frame);
            }
        }

        Tracker.ResetEntitiesKeepSchema();
        if (instanceBaselineFullPacket is not null)
        {
            Tracker.LoadInstanceBaselineSnapshot(instanceBaselineFullPacket);
        }

        SeedCheckpoint(checkpoint, successor);
        _nextFrameIndex = IndexAfter(checkpoint);
    }

    // A list-backed layer seeks on past the checkpoint within its own list; one fed by its owner
    // only records where the stream stands.
    private int IndexAfter(DemoFrame checkpoint)
    {
        if (_frames is null)
        {
            return checkpoint.FrameNumber + 1;
        }

        for (int i = 0; i < _frames.Count; i++)
        {
            if (ReferenceEquals(_frames[i], checkpoint))
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException("PrimeFromCheckpoint: the checkpoint is not in this layer's frame list.");
    }

    private void SeedCheckpoint(DemoFrame checkpoint, DemoFrame? successor)
    {
        Tracker.ProcessFullPacketCheckpoint(checkpoint);

        // A same-tick successor's delta would be skipped by the first tick-gated seek, while a
        // sequential decode folds it into the same frame. Loud, never silently handled.
        if (successor is not null && successor.ServerTick == checkpoint.ServerTick)
        {
            throw new InvalidOperationException(
                $"PrimeFromCheckpoint: full-packet frame {checkpoint.FrameNumber} (tick {checkpoint.ServerTick}) " +
                $"has a same-tick successor at frame {successor.FrameNumber}; the checkpoint snapshot would miss its delta. " +
                "The chunked decode assumes full packets have no same-tick successor.");
        }
    }

    private IReadOnlyList<DemoFrame> RequireFrames() =>
        _frames ?? throw new InvalidOperationException(
            "This layer was built without frames; feed it through Apply, or build it over a frame list to seek.");

    // ── Zero-allocation frame slice ───────────────────────────────────────────

    private sealed class FrameSlice(IReadOnlyList<DemoFrame> source, int start, int count)
        : IReadOnlyList<DemoFrame>
    {
        public int Count => count;

        public IEnumerator<DemoFrame> GetEnumerator()
        {
            for (int i = 0; i < count; i++)
            {
                yield return source[start + i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public DemoFrame this[int index] => source[start + index];
    }
}
