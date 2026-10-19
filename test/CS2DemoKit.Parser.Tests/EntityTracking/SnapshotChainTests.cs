#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The delta-chain check sequential playback uses to find a dropped snapshot. The shapes are the
///     ones a GOTV recording produces: a non-delta first packet, deltas each naming the previous
///     packet's server tick, and one delta naming a tick the recording never wrote.
/// </summary>
public class SnapshotChainTests
{
    [Test]
    public async Task AnUnbrokenChain_StaysWhole()
    {
        SnapshotChain chain = new();
        chain.Observe(false, false, 0, true, 100);
        chain.Observe(true, true, 100, true, 101);
        chain.Observe(true, true, 101, true, 103);
        chain.Observe(true, true, 103, true, 104);

        await Assert.That(chain.Broken).IsFalse();
    }

    [Test]
    public async Task ADeltaFromAMissingTick_Breaks_AndStaysBroken()
    {
        SnapshotChain chain = new();
        chain.Observe(false, false, 0, true, 254456);
        chain.Observe(true, true, 254456, true, 254457);

        // dust2: the snapshot at 254458 was dropped, and the next delta is built on it.
        chain.Observe(true, true, 254458, true, 254459);
        await Assert.That(chain.Broken).IsTrue();

        // Every later delta chains onto the one before, so a per-message check would clear here.
        chain.Observe(true, true, 254459, true, 254460);
        await Assert.That(chain.Broken).IsTrue();
    }

    [Test]
    public async Task ASeed_RestartsTheChain_AtTheFullSnapshotTick()
    {
        SnapshotChain chain = new();
        chain.Observe(false, false, 0, true, 10);
        chain.Observe(true, true, 12, true, 13);
        await Assert.That(chain.Broken).IsTrue();

        chain.Seed(true, 20);
        await Assert.That(chain.Broken).IsFalse();

        chain.Observe(true, true, 20, true, 21);
        await Assert.That(chain.Broken).IsFalse();

        chain.Observe(true, true, 20, true, 22);
        await Assert.That(chain.Broken).IsTrue();
    }

    [Test]
    public async Task WithNoKnownBase_NothingBreaks()
    {
        SnapshotChain chain = new();
        chain.Observe(true, true, 500, true, 501);
        await Assert.That(chain.Broken).IsFalse().Because("the first delta has no base to check against");

        chain.Observe(true, true, 501, false, 0);
        chain.Observe(true, true, 777, true, 778);
        await Assert.That(chain.Broken).IsFalse().Because("a packet without server_tick leaves the base unknown");

        chain.Observe(true, false, 0, true, 779);
        await Assert.That(chain.Broken).IsFalse().Because("a delta without delta_from names no base");
    }

    [Test]
    public async Task ANonDeltaPacket_NeverBreaks_ButMovesTheBase()
    {
        SnapshotChain chain = new();
        chain.Observe(false, false, 0, true, 40);
        chain.Observe(false, false, 0, true, 90);
        chain.Observe(true, true, 90, true, 91);

        await Assert.That(chain.Broken).IsFalse();
    }
}
