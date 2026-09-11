#region

using System.Numerics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Pins that reading the projectile slots through <see cref="ProjectileSlotIndex" /> yields the
///     digest the full entity walk yields, frame for frame, on a real demo. The walk is kept in
///     <see cref="EntityDigestExtractor" /> as the oracle (a null index), so both arms here read the
///     SAME layer at the SAME frame and any difference is the index's, not the decode's.
///     <para>
///         The two ways the index can be wrong are both exercised: missing a projectile that was
///         already alive when the index first met the tracker (a chunk worker primed mid-demo, a
///         reset layer), and missing one created after a layer reset swapped the tracker under a
///         live subscription. Each has a test that fails when that path is removed.
///     </para>
/// </summary>
[Category("Integration")]
public class ProjectileSlotIndexTests
{
    private static ParsedDemo RequireSample() =>
        DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

    private static PerPawnDeltaState EmptyDelta() => new(DigestColumnLayout.For([]));

    private static string? Diff(EntityFrameDigest walk, EntityFrameDigest indexed)
    {
        if (!walk.Molotovs.SequenceEqual(indexed.Molotovs))
        {
            return $"molotovs walk={walk.Molotovs.Length} indexed={indexed.Molotovs.Length}";
        }

        if (!walk.Smokes.SequenceEqual(indexed.Smokes))
        {
            return $"smokes walk={walk.Smokes.Length} indexed={indexed.Smokes.Length}";
        }

        return null;
    }

    [Test]
    public async Task IndexedRead_MatchesTheFullWalk_OnEveryFrame_OfTheSampleDemo() =>
        await AssertParityOverEveryFrame(RequireSample());

    /// <summary>
    ///     The same parity over the reference demo when the corpus is present (a full match, not a
    ///     trim); skips otherwise, and says so.
    /// </summary>
    [Test]
    public async Task IndexedRead_MatchesTheFullWalk_OnEveryFrame_OfTheReferenceDemo()
    {
        string path = DemoTestHelper.RequireDemo();
        if (Path.GetFileName(path) == DemoTestHelper.SampleDemoFileName)
        {
            throw new SkipTestException("no reference demo beyond the sample; the sample test already covers it");
        }

        await AssertParityOverEveryFrame(DemoTestHelper.GetOrParse(path));
    }

    private static async Task AssertParityOverEveryFrame(ParsedDemo demo)
    {
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        EntityStateLayer layer = new(frames);
        PerPawnDeltaState delta = EmptyDelta();
        ProjectileSlotIndex index = new();

        int mismatches = 0;
        string? first = null;
        int framesWithSmoke = 0, framesWithMolotov = 0;
        for (int n = 0; n < frames.Count; n++)
        {
            layer.SeekToTick(frames[n].ServerTick);
            EntityFrameDigest walk = EntityDigestExtractor.Build(layer, delta, [], true, true);
            EntityFrameDigest indexed = EntityDigestExtractor.Build(layer, delta, [], true, true, index);

            if (walk.Smokes.Length > 0)
            {
                framesWithSmoke++;
            }

            if (walk.Molotovs.Length > 0)
            {
                framesWithMolotov++;
            }

            if (Diff(walk, indexed) is { } diff)
            {
                mismatches++;
                first ??= $"frame {n} (tick {frames[n].ServerTick}): {diff}";
            }
        }

        Console.WriteLine(
            $"{frames.Count:N0} frames, {framesWithSmoke:N0} with an active smoke, {framesWithMolotov:N0} with a live molotov, "
            + $"{index.SeedWalks} seed walk(s)");
        await Assert.That(mismatches).IsEqualTo(0).Because(first ?? "");
        await Assert.That(framesWithSmoke).IsGreaterThan(0)
            .Because("a demo with no smoke at all would make this parity pass while checking nothing");
        await Assert.That(index.SeedWalks).IsEqualTo(1)
            .Because("one layer, one tracker: the index should have walked the entity set exactly once");
    }

    /// <summary>
    ///     An index that first meets a tracker mid-demo must see the projectiles already alive in
    ///     it, which only the seed walk can provide: no creation event will ever fire for them.
    ///     This is the chunk-worker case, where the layer is primed from a checkpoint before the
    ///     first digest is built.
    /// </summary>
    [Test]
    public async Task IndexBoundMidDemo_SeesTheProjectilesAlreadyAlive()
    {
        ParsedDemo demo = RequireSample();
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        EntityStateLayer layer = new(frames);
        PerPawnDeltaState delta = EmptyDelta();

        int at = FirstFrameWithProjectiles(layer, delta, frames, 0, out EntityFrameDigest walk);
        await Assert.That(at).IsGreaterThanOrEqualTo(0).Because("the sample demo carries at least one smoke or molotov");

        // The layer already sits on that frame; a fresh index meets a tracker with the projectile alive.
        ProjectileSlotIndex late = new();
        EntityFrameDigest indexed = EntityDigestExtractor.Build(layer, delta, [], true, true, late);

        Console.WriteLine($"bound at frame {at}: walk molotovs={walk.Molotovs.Length} smokes={walk.Smokes.Length}; "
                          + $"index slots={late.Slots.Length}");
        await Assert.That(Diff(walk, indexed)).IsNull();
        await Assert.That(late.Slots.Length).IsGreaterThan(0);
    }

    /// <summary>
    ///     A layer reset replaces its tracker, and an index still subscribed to the old one would
    ///     never hear about projectiles created in the new one. After the reset the index is
    ///     advanced to a frame whose projectile set differs from the one it was bound on, so a
    ///     stale subscription cannot pass by coincidence.
    /// </summary>
    [Test]
    public async Task IndexRebinds_WhenTheLayerIsReset()
    {
        ParsedDemo demo = RequireSample();
        IReadOnlyList<DemoFrame> frames = demo.Frames;
        EntityStateLayer layer = new(frames);
        PerPawnDeltaState delta = EmptyDelta();
        ProjectileSlotIndex index = new();

        int at = FirstFrameWithProjectiles(layer, delta, frames, 0, out EntityFrameDigest walkAt);
        await Assert.That(at).IsGreaterThanOrEqualTo(0);
        EntityFrameDigest indexedAt = EntityDigestExtractor.Build(layer, delta, [], true, true, index);
        await Assert.That(Diff(walkAt, indexedAt)).IsNull();
        int[] slotsAt = index.Slots.ToArray();

        // A later frame whose projectile SLOTS differ from the bound frame's AND whose walked
        // digest is non-empty, so the new tracker has created something the old subscription
        // would have missed and the digest comparison below cannot pass on two empty lists (a
        // smoke still in flight is a slot in the index but not an entry in the digest).
        layer.Reset();
        int later = -1;
        EntityFrameDigest? walkLater = null;
        for (int n = at + 1; n < frames.Count; n++)
        {
            layer.SeekToTick(frames[n].ServerTick);
            EntityFrameDigest w = EntityDigestExtractor.Build(layer, delta, [], true, true);
            ProjectileSlotIndex probe = new();
            EntityDigestExtractor.Build(layer, delta, [], true, true, probe);
            if (w.Molotovs.Length + w.Smokes.Length > 0 && !probe.Slots.SequenceEqual(slotsAt))
            {
                later = n;
                walkLater = w;
                break;
            }
        }

        await Assert.That(later).IsGreaterThan(at).Because("the sample demo has a second projectile in a different slot");

        EntityFrameDigest indexedLater = EntityDigestExtractor.Build(layer, delta, [], true, true, index);
        Console.WriteLine($"bound at frame {at} slots [{string.Join(",", slotsAt)}], reset, read at frame {later} "
                          + $"slots [{string.Join(",", index.Slots.ToArray())}], seed walks {index.SeedWalks}");
        await Assert.That(Diff(walkLater!, indexedLater)).IsNull();
        await Assert.That(index.SeedWalks).IsEqualTo(2).Because("the reset swapped the tracker, which is a rebind");
    }

    // Advances the layer from frame `from` until the walk finds a projectile; returns that frame
    // (the layer is left on it) or -1.
    private static int FirstFrameWithProjectiles(
        EntityStateLayer layer, PerPawnDeltaState delta, IReadOnlyList<DemoFrame> frames, int from,
        out EntityFrameDigest walk)
    {
        for (int n = from; n < frames.Count; n++)
        {
            layer.SeekToTick(frames[n].ServerTick);
            walk = EntityDigestExtractor.Build(layer, delta, [], true, true);
            if (walk.Molotovs.Length > 0 || walk.Smokes.Length > 0)
            {
                return n;
            }
        }

        walk = new EntityFrameDigest();
        return -1;
    }
}
