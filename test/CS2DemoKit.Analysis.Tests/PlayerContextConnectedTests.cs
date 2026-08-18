#region

using CS2DemoKit.Analysis.Building;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit tests for the disconnect-ghost defect fix: <see cref="PlayerContextIndex.PlayerContext.Connected" />
///     is excluded from <see cref="PlayerContextIndex.ResetRoundState" />, so a mid-match disconnect no
///     longer resurrects a ghost as alive in later rounds. Before the fix, ResetRoundState set every
///     registered player's <c>IsAlive</c> back to <c>true</c> each round, so a disconnected player kept
///     inflating <see cref="PlayerContextIndex.CountAlive" /> (and hence clutch detection) forever.
/// </summary>
[Category("Unit")]
public class PlayerContextConnectedTests
{
    private static PlayerContextIndex FiveVsFive()
    {
        PlayerContextIndex index = new();
        for (int slot = 0; slot < 5; slot++)
        {
            index.Register(slot, new PlayerContextIndex.PlayerContext(slot, 2)); // T
        }

        for (int slot = 5; slot < 10; slot++)
        {
            index.Register(slot, new PlayerContextIndex.PlayerContext(slot, 3)); // CT
        }

        return index;
    }

    /// <summary>Freshly registered players are Connected and counted alive.</summary>
    [Test]
    public async Task FreshPlayers_AreConnectedAndAlive()
    {
        PlayerContextIndex index = FiveVsFive();

        await Assert.That(index.CountAlive(2)).IsEqualTo(5);
        await Assert.That(index.CountAlive(3)).IsEqualTo(5);
        await Assert.That(index.CountConnected(2)).IsEqualTo(5);
    }

    /// <summary>
    ///     The defect the fix closes: a player who disconnects in round 1 must NOT be counted alive in
    ///     round 2, even though <see cref="PlayerContextIndex.ResetRoundState" /> resurrects IsAlive.
    /// </summary>
    [Test]
    public async Task DisconnectedPlayer_NotCountedAlive_AfterRoundReset()
    {
        PlayerContextIndex index = FiveVsFive();

        // Round 1: slot 0 (T) disconnects.
        index.MarkDisconnected(0);
        await Assert.That(index.CountAlive(2)).IsEqualTo(4);
        await Assert.That(index.CountConnected(2)).IsEqualTo(4);

        // Round 2 starts: ResetRoundState resurrects IsAlive but must leave Connected cleared.
        index.ResetRoundState();
        await Assert.That(index.CountAlive(2)).IsEqualTo(4); // pre-fix bug: would be 5
        await Assert.That(index.CountConnected(2)).IsEqualTo(4);
    }

    /// <summary>Alive counts recompute live across kills within a round (single-writer via MarkDead).</summary>
    [Test]
    public async Task CountAlive_RecomputesAcrossKillsWithinRound()
    {
        PlayerContextIndex index = FiveVsFive();

        index.MarkDead(1); // a T dies
        index.MarkDead(2); // another T dies
        await Assert.That(index.CountAlive(2)).IsEqualTo(3);
        await Assert.That(index.CountAlive(3)).IsEqualTo(5);

        index.ResetRoundState(); // next round: everyone alive again (all still connected)
        await Assert.That(index.CountAlive(2)).IsEqualTo(5);
    }

    /// <summary>A disconnected sole survivor is not treated as the lone-alive clutcher.</summary>
    [Test]
    public async Task FindLoneAlive_ExcludesDisconnected()
    {
        PlayerContextIndex index = FiveVsFive();

        // Kill 4 of 5 T players, leaving slot 0 as the lone survivor.
        index.MarkDead(1);
        index.MarkDead(2);
        index.MarkDead(3);
        index.MarkDead(4);
        await Assert.That(index.FindLoneAlive(2)).IsEqualTo(0);

        // Now slot 0 disconnects — there is no lone survivor anymore.
        index.MarkDisconnected(0);
        await Assert.That(index.FindLoneAlive(2)).IsEqualTo(-1);
        await Assert.That(index.CountAlive(2)).IsEqualTo(0);
    }

    /// <summary>Reconnect (connect/spawn) re-enables inclusion in the Connected-gated aggregates.</summary>
    [Test]
    public async Task Reconnect_RestoresAliveCount()
    {
        PlayerContextIndex index = FiveVsFive();

        index.MarkDisconnected(0);
        await Assert.That(index.CountConnected(2)).IsEqualTo(4);

        index.MarkConnected(0);
        await Assert.That(index.CountConnected(2)).IsEqualTo(5);
        await Assert.That(index.CountAlive(2)).IsEqualTo(5);
    }
}
