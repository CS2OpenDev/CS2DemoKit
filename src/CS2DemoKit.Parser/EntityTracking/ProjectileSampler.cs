#region

using System.Numerics;
using CS2DemoKit.Parser.Entities;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     One grenade projectile at one frame: where it is, who threw it, and where its flight began.
/// </summary>
/// <param name="FrameIndex">0-based count of the frames the walk had read when it took the sample.</param>
/// <param name="Tick">
///     The frame clock: the <see cref="DemoFrame.ServerTick" /> of the frame the sample was taken
///     on. The same clock as <c>GameEvent.GameTick</c>; <c>GameEvent.ServerTick</c> is this plus
///     <see cref="ParsedDemo.ServerStartTick" />. Not unique: several frames can share one, so an
///     exact join with events compares <c>GameEvent.FrameNumber</c> with <c>FrameIndex</c>.
/// </param>
/// <param name="EntityIndex">The projectile's entity index. Reused by later entities once it is removed.</param>
/// <param name="Serial">
///     The entity serial. <c>(EntityIndex, Serial)</c> identifies one projectile for its whole life.
/// </param>
/// <param name="ClassName">One of <see cref="GrenadeProjectileClasses" />.</param>
/// <param name="ThrowerSlot">
///     The thrower's player slot, 0-63, from <see cref="PawnLookup.ResolveThrowerSlot" />; -1 until it
///     first resolves. Once resolved it is held for the rest of the projectile's life, because the
///     live chain breaks when the thrower dies (the pawn's controller handle goes invalid) while the
///     grenade is still in the air.
/// </param>
/// <param name="Position">
///     World position reconstructed from the projectile's cell and offset fields. Null when a field
///     is unseen or any cell index is 0, which puts the axis outside every map and means the cell is
///     garbage rather than a position.
/// </param>
/// <param name="InitialPosition">
///     <c>m_vInitialPosition</c>, the point the grenade left the hand, about 35 to 80 units from the
///     thrower's pawn origin. Null when unseen.
/// </param>
/// <param name="InitialVelocity"><c>m_vInitialVelocity</c> at release. Null when unseen.</param>
/// <param name="Bounces"><c>m_nBounces</c>, or 0 when unseen.</param>
/// <param name="Created">
///     True on the first sample for this <c>(EntityIndex, Serial)</c>, on the frame the entity
///     appeared. That is the throw for every projectile except those already in flight when the
///     recording began, which appear on the first entity frame with <c>Position</c> far from
///     <c>InitialPosition</c>.
/// </param>
/// <param name="Removed">
///     True on the one sample yielded on the frame the projectile's slot was found empty or held by
///     another entity. <c>FrameIndex</c> and <c>Tick</c> are that frame's; every other value is the
///     last one seen, on the frame before, so <c>Position</c> is where the projectile ended.
/// </param>
public readonly record struct ProjectileSample(
    int FrameIndex,
    int Tick,
    int EntityIndex,
    int Serial,
    string ClassName,
    int ThrowerSlot,
    Vector3? Position,
    Vector3? InitialPosition,
    Vector3? InitialVelocity,
    int Bounces,
    bool Created,
    bool Removed);

/// <summary>
///     Streams every grenade projectile (the five <see cref="GrenadeProjectileClasses" />) over a
///     whole demo, from the frame it appears to the frame it is removed. The sibling of
///     <see cref="PositionSampler" /> for grenades: it assembles slot lifecycle, thrower resolution
///     and cell-to-world so consumers do not re-derive them against <see cref="EntityTracker" />.
///     <para>
///         Row assembly stays with the consumer: the release tick (the <c>weapon_fire</c> event,
///         7 to 15 ticks before creation), the detonation join, and jump-throw classification.
///     </para>
///     <para>
///         Slots are tracked through <see cref="EntityTracker.EntityCreated" />, so the walk reads
///         only live projectiles (a handful at a time) and never scans the entity set. Measured
///         on a 154,869-frame de_nuke (Release, warm, 229 projectiles, 148,495 samples at stride
///         1): 0.54 s and 98 MB over a retained <see cref="ParsedDemo" />, on top of the 0.25 s
///         and 578 MB the parse took, against 0.7 s and 251 MB end to end from bytes through a
///         <see cref="DemoReader" /> with <see cref="DecodePlan.EntityReplay" />. The forward
///         route is the cheaper one when nothing else needs the retained frames.
///     </para>
/// </summary>
public static class ProjectileSampler
{
    /// <summary>
    ///     Samples every projectile in <paramref name="demo" />, in frame order then ascending
    ///     entity index. Lazy, and the walk restarts from frame 0 on a second enumeration.
    ///     <para>
    ///         <paramref name="frameStride" /> thins the in-flight samples only: a sample is yielded
    ///         on frames whose index is a multiple of the stride, and the <c>Created</c> and
    ///         <c>Removed</c> samples are always yielded on their own frame. A molotov can live 20
    ///         frames, so a plain stride would lose short lives. Every frame is still decoded and
    ///         every tracked projectile read, so a strided walk equals the stride-1 walk filtered to
    ///         <c>FrameIndex % frameStride == 0 || Created || Removed</c>. This differs from
    ///         <see cref="PositionSampler" />, whose stride drops everything between.
    ///     </para>
    ///     <para>
    ///         When one frame replaces a projectile with another entity in the same slot, the old
    ///         projectile's <c>Removed</c> sample comes before the new one's <c>Created</c> sample.
    ///         A projectile still alive at <paramref name="maxFrames" /> or at the end of the demo
    ///         gets no <c>Removed</c> sample. There is no late start, for the reason given on
    ///         <see cref="PositionSampler.Walk" />; walk from frame 0 and filter by tick.
    ///     </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="frameStride" /> is below 1, or <paramref name="maxFrames" /> is negative.
    /// </exception>
    public static IEnumerable<ProjectileSample> Walk(ParsedDemo demo, int frameStride = 1,
        int maxFrames = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(demo);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameStride, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        return Iterate(demo.Frames, frameStride, maxFrames);
    }

    /// <summary>
    ///     The same walk over a forward frame source, read once through
    ///     <see cref="IDemoFrameSource.TryReadNext" />. Open a <see cref="DemoReader" /> with
    ///     <see cref="DecodePlan.EntityReplay" /> to skip decoding what the walk does not read. The
    ///     source must be at its first frame, and enumerating the result consumes it; the caller
    ///     keeps ownership and disposes it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     <paramref name="frameStride" /> is below 1, or <paramref name="maxFrames" /> is negative.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    ///     <paramref name="source" /> is a <see cref="DemoReader" /> that has already read or peeked
    ///     a frame. A walk that starts mid-stream builds on entity deltas it never saw.
    /// </exception>
    public static IEnumerable<ProjectileSample> Walk(IDemoFrameSource source, int frameStride = 1,
        int maxFrames = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameStride, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFrames);
        if (source is DemoReader { Started: true })
        {
            throw new InvalidOperationException(
                "The reader has already read a frame; the projectile walk must start at the first frame.");
        }

        return Iterate(ReadAll(source), frameStride, maxFrames);

        static IEnumerable<DemoFrame> ReadAll(IDemoFrameSource s)
        {
            while (s.TryReadNext(out DemoFrame? frame))
            {
                yield return frame;
            }
        }
    }

    private static IEnumerable<ProjectileSample> Iterate(IEnumerable<DemoFrame> frames, int frameStride,
        int maxFrames)
    {
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();

        // Indices whose slot saw a projectile enter this frame. Checked once the frame is fully
        // applied, so a slot created and deleted within the frame is dropped here.
        List<int> pending = [];
        tracker.EntityCreated += (index, state) =>
        {
            if (GrenadeProjectileClasses.Contains(state.ClassName))
            {
                pending.Add(index);
            }
        };

        // Kept ascending by entity index: the order a frame's samples come out in.
        List<Tracked> tracked = [];
        List<ProjectileSample> buffer = [];

        int frameIndex = 0;
        // Checked before each read, so a forward source is not advanced past maxFrames.
        using IEnumerator<DemoFrame> cursor = frames.GetEnumerator();
        while (frameIndex < maxFrames && cursor.MoveNext())
        {
            DemoFrame frame = cursor.Current;
            pending.Clear();
            tracker.AdvanceOneFrame(frame);

            int tick = frame.ServerTick;
            bool emit = frameIndex % frameStride == 0;
            EntitySet entities = tracker.CurrentEntities;
            buffer.Clear();

            for (int t = 0; t < tracked.Count; t++)
            {
                Tracked p = tracked[t];
                EntityState? ent = entities[p.Index];
                if (ent is null || ent.Serial != p.Serial || !string.Equals(ent.ClassName, p.ClassName, StringComparison.Ordinal))
                {
                    buffer.Add(p.Last with { FrameIndex = frameIndex, Tick = tick, Created = false, Removed = true });
                    tracked.RemoveAt(t--);
                    continue;
                }

                p.Last = Read(tracker, ent, p, frameIndex, tick, false);
                if (emit)
                {
                    buffer.Add(p.Last);
                }
            }

            if (pending.Count > 0)
            {
                pending.Sort();
                for (int k = 0; k < pending.Count; k++)
                {
                    int index = pending[k];
                    if (k > 0 && pending[k - 1] == index)
                    {
                        continue;
                    }

                    EntityState? ent = entities[index];
                    if (ent is null || !GrenadeProjectileClasses.Contains(ent.ClassName) || IsTracked(tracked, index))
                    {
                        continue;
                    }

                    Tracked p = new(index, ent.Serial, ent.ClassName);
                    p.Last = Read(tracker, ent, p, frameIndex, tick, true);
                    Insert(tracked, p);
                    buffer.Add(p.Last);
                }
            }

            if (buffer.Count > 1)
            {
                buffer.Sort(static (a, b) => a.EntityIndex != b.EntityIndex
                    ? a.EntityIndex.CompareTo(b.EntityIndex)
                    : b.Removed.CompareTo(a.Removed));
            }

            foreach (ProjectileSample sample in buffer)
            {
                yield return sample;
            }

            frameIndex++;
        }
    }

    private static ProjectileSample Read(EntityTracker tracker, EntityState ent, Tracked p, int frameIndex,
        int tick, bool created)
    {
        if (p.ThrowerSlot < 0)
        {
            p.ThrowerSlot = PawnLookup.ResolveThrowerSlot(tracker, ent);
        }

        return new ProjectileSample(frameIndex, tick, p.Index, p.Serial, p.ClassName, p.ThrowerSlot,
            PositionUtil.CellToWorldCore(ent, true),
            ent.TryGet<Vector3>("m_vInitialPosition"),
            ent.TryGet<Vector3>("m_vInitialVelocity"),
            ent.TryGet<int>("m_nBounces") ?? 0,
            created,
            false);
    }

    // After the removal pass, any entry still at this index is the entity now in the slot (same
    // serial and class), so a second enter-PVS for it is not a new projectile.
    private static bool IsTracked(List<Tracked> tracked, int index)
    {
        foreach (Tracked p in tracked)
        {
            if (p.Index == index)
            {
                return true;
            }
        }

        return false;
    }

    private static void Insert(List<Tracked> tracked, Tracked p)
    {
        int at = tracked.Count;
        while (at > 0 && tracked[at - 1].Index > p.Index)
        {
            at--;
        }

        tracked.Insert(at, p);
    }

    private sealed class Tracked(int index, int serial, string className)
    {
        public int Index { get; } = index;
        public int Serial { get; } = serial;
        public string ClassName { get; } = className;
        public int ThrowerSlot { get; set; } = -1;
        public ProjectileSample Last { get; set; }
    }
}
