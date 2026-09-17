#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

using CS2OpenSchema.Protos;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Unit pins for <see cref="KillTeamEnrichmentEdge" />'s team classification — in particular
///     the S7 (assists mixed sign) fix: <c>enrich.kill.was_enemy_assist</c> tests the
///     ASSISTER against the victim, independently of the killer-vs-victim classification that
///     drives <c>was_enemy_kill</c> / <c>was_team_kill</c> / <c>was_self_kill</c>. Every scenario
///     below is a shape observed on the bench suite against the reference goldens (demo + tick
///     cited per test). Pure in-memory — no demo file.
/// </summary>
[Category("Unit")]
public class KillTeamEnrichmentEdgeTests
{
    private const int NoPlayer = 65535; // GOTV "no assister" sentinel observed on the bench demos

    // Slots: 0,1 on team 2 (T); 5,6 on team 3 (CT); 9 unknown (never registered → team 0).
    private static PlayerContextIndex TwoVsTwo()
    {
        PlayerContextIndex index = new();
        index.Register(0, new PlayerContextIndex.PlayerContext(0, 2));
        index.Register(1, new PlayerContextIndex.PlayerContext(1, 2));
        index.Register(5, new PlayerContextIndex.PlayerContext(5, 3));
        index.Register(6, new PlayerContextIndex.PlayerContext(6, 3));
        return index;
    }

    private sealed record Fixture(
        KillTeamEnrichmentEdge Edge,
        TransientBoolNode EnemyKill,
        TransientBoolNode TeamKill,
        TransientBoolNode SelfKill,
        TransientBoolNode EnemyAssist,
        TransientValueNode<int> TicksSinceSpot,
        TransientBoolNode TradeKill,
        TransientBoolNode FlashKill);

    private static Fixture Build(PlayerContextIndex index)
    {
        TransientBoolNode enemyKill = new("enrich.kill.was_enemy_kill");
        TransientBoolNode teamKill = new("enrich.kill.was_team_kill");
        TransientBoolNode selfKill = new("enrich.kill.was_self_kill");
        TransientBoolNode tradeKill = new("enrich.kill.was_trade_kill");
        TransientValueNode<int> tradedSlot = new("enrich.kill.traded_player_slot", -1);
        TransientBoolNode flashKill = new("enrich.kill.was_flash_kill");
        TransientValueNode<int> flashSlot = new("enrich.kill.flash_attacker_slot", -1);
        TransientBoolNode enemyAssist = new("enrich.kill.was_enemy_assist");
        TransientValueNode<int> killSinceSpot = new(
            "enrich.kill.ticks_since_spot", AimShotContextEdge.NoSpotSentinel);

        GenericBoolNode root = new("root");
        KillTeamEnrichmentEdge edge = new(
            root, index, enemyKill, teamKill, selfKill,
            tradeKill, tradedSlot, flashKill, flashSlot, killSinceSpot, enemyAssist);
        return new Fixture(
            edge, enemyKill, teamKill, selfKill, enemyAssist, killSinceSpot, tradeKill, flashKill);
    }

    /// <summary>
    ///     Dispatches one <c>player_death</c> with its own tick and its frame's header tick a tick
    ///     APART, which is what separates the outputs this edge measures on one from those it
    ///     measures on the other. Both are the frame clock (a frame header tick already is it, and
    ///     GameTick reaches it as <c>ServerTick - ServerStartTick</c>) but they are not the same
    ///     instant: the server stamps an event during its simulation and the frame that carries it
    ///     can be the next one. The defaults take the direction the demos show — 21 of the bundled
    ///     sample's 22 <c>player_death</c> events sit one tick below their frame's header.
    /// </summary>
    private static bool Apply(
        Fixture f, int victim, int killer, int assister, string weapon = "ak47",
        int eventTick = 99, int frameTick = 100)
    {
        GameEvent death = TestGameEvents.PlayerDeath(
            victim, killer, assister, weapon, dmgHealth: (short)100, gameTick: eventTick);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(death);
        DemoFrame frame = new()
        {
            CommandKind = EDemoCommands.DemPacket,
            FrameNumber = 0,
            ServerTick = frameTick,
            RawStart = 0,
            RawLength = 1,
            HeaderLength = 1,
            IsCompressed = false,
            MessageList = [msg]
        };
        return f.Edge.TryApply(new EvaluationContext(msg, frame));
    }

    /// <summary>
    ///     Team-damage assist (mirage bench demo, tick 11027): assister on the VICTIM's own team,
    ///     victim killed by an enemy. was_enemy_kill fires (killer↔victim are enemies) but
    ///     was_enemy_assist must NOT — this is the S7 +1 overcount shape (the assists stat does
    ///     not credit team-damage assists).
    /// </summary>
    [Test]
    public async Task TeamDamageAssist_EnemyKillFires_EnemyAssistDoesNot()
    {
        Fixture f = Build(TwoVsTwo());
        // victim slot 1 (T) killed by slot 5 (CT), assisted by teammate slot 0 (T)
        bool applied = Apply(f, victim: 1, killer: 5, assister: 0);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.EnemyKill.IsActive).IsTrue();
        await Assert.That(f.EnemyAssist.IsActive).IsFalse()
            .Because("the assister is on the victim's own team — not an enemy assist");
    }

    /// <summary>
    ///     Enemy assister on a TEAMKILL (ancient bench demo tick 115284; inferno tick 81688):
    ///     killer and victim share a team, the assister is an enemy of the victim. was_team_kill
    ///     fires and was_enemy_kill does not — but was_enemy_assist MUST fire. This is the S7 −1
    ///     undercount shape (the assists stat credits the enemy assister).
    /// </summary>
    [Test]
    public async Task EnemyAssisterOnTeamkill_EnemyAssistFires()
    {
        Fixture f = Build(TwoVsTwo());
        // victim slot 6 (CT) teamkilled by slot 5 (CT), assisted by enemy slot 0 (T)
        bool applied = Apply(f, victim: 6, killer: 5, assister: 0);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.TeamKill.IsActive).IsTrue();
        await Assert.That(f.EnemyKill.IsActive).IsFalse();
        await Assert.That(f.EnemyAssist.IsActive).IsTrue()
            .Because("the assister is an enemy of the victim even though the kill was a teamkill");
    }

    /// <summary>Plain enemy kill with an assister on the killer's team: both bools fire.</summary>
    [Test]
    public async Task NormalEnemyAssist_BothFire()
    {
        Fixture f = Build(TwoVsTwo());
        // victim slot 1 (T) killed by slot 5 (CT), assisted by killer's teammate slot 6 (CT)
        bool applied = Apply(f, victim: 1, killer: 5, assister: 6);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.EnemyKill.IsActive).IsTrue();
        await Assert.That(f.EnemyAssist.IsActive).IsTrue();
    }

    /// <summary>No assister (GOTV sentinel 65535): was_enemy_assist must stay inactive.</summary>
    [Test]
    public async Task NoAssister_EnemyAssistDoesNotFire()
    {
        Fixture f = Build(TwoVsTwo());
        bool applied = Apply(f, victim: 1, killer: 5, assister: NoPlayer);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.EnemyKill.IsActive).IsTrue();
        await Assert.That(f.EnemyAssist.IsActive).IsFalse();
    }

    /// <summary>
    ///     Suicide with an enemy assister (nuke bench demo, tick 89840: world death, enemy
    ///     assister). The ENRICHMENT still fires — it describes the assister↔victim relationship,
    ///     not the kill shape. Exclusion from assist counts happens at the view level: the assist
    ///     view bakes <c>Attacker != UserId</c>, which the reference goldens confirm
    ///     (shitstainsteve pins 2, not 3, on the nuke demo).
    /// </summary>
    [Test]
    public async Task SuicideWithEnemyAssister_EnrichmentFires_SelfKillClassified()
    {
        Fixture f = Build(TwoVsTwo());
        // victim slot 1 (T) suicides (killer == victim), assister slot 5 (CT) is an enemy
        bool applied = Apply(f, victim: 1, killer: 1, assister: 5, weapon: "world");

        await Assert.That(applied).IsTrue();
        await Assert.That(f.SelfKill.IsActive).IsTrue();
        await Assert.That(f.EnemyKill.IsActive).IsFalse();
        await Assert.That(f.TeamKill.IsActive).IsFalse();
        await Assert.That(f.EnemyAssist.IsActive).IsTrue()
            .Because("assister↔victim enmity is independent of the kill shape; the assist view's "
                     + "baked Attacker != UserId is what excludes suicides from assist counts");
    }

    /// <summary>
    ///     Time-to-kill is measured from the killer's latched contact on the FRAME HEADER tick, not
    ///     on the death event's own <c>GameTick</c>.
    ///     <para>
    ///         <c>LastSpotTick</c> is latched from a SYNTHESIZED <c>enemy_spotted</c>, which
    ///         <c>VisibilityTransitionScanner</c> stamps straight off <c>DemoFrame.ServerTick</c>:
    ///         it carries no simulation stamp of its own and lands exactly on its frame. The header
    ///         is therefore the instant it is comparable against, and it is the one
    ///         <c>AimShotContextEdge.EmitSpotPairing</c> measures this column's shot-anchored twin
    ///         on. Differencing the event's own tick against it reads every interval short.
    ///     </para>
    /// </summary>
    [Test]
    public async Task KillAfterAContact_MeasuresTheIntervalOnTheFrameHeaderTick()
    {
        PlayerContextIndex index = TwoVsTwo();
        Fixture f = Build(index);
        index.TryGet(5, out PlayerContextIndex.PlayerContext? killer);
        killer!.LastSpotTick = 40;

        bool applied = Apply(f, victim: 1, killer: 5, assister: NoPlayer);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.TicksSinceSpot.Value).IsEqualTo(60)
            .Because("the contact was latched on frame 40 and this kill's frame header is 100; the "
                     + "death event's own tick, 99, would report the interval one tick short");
    }

    /// <summary>
    ///     The case that makes the clock mismatch worth correcting rather than rounding off: a
    ///     killer who spots and kills inside the SAME frame. The death event's tick sits a tick
    ///     below the frame that carries it, so against a contact latched on that frame's header the
    ///     difference is -1 — which fails the guard and reports the sentinel, dropping the fastest
    ///     time-to-kill in the demo out of the population rather than recording it as 0.
    /// </summary>
    [Test]
    public async Task KillOnTheSameFrameAsTheContact_ReadsZero_NotTheSentinel()
    {
        PlayerContextIndex index = TwoVsTwo();
        Fixture f = Build(index);
        index.TryGet(5, out PlayerContextIndex.PlayerContext? killer);
        killer!.LastSpotTick = 100; // the header of the frame this kill is delivered on

        bool applied = Apply(f, victim: 1, killer: 5, assister: NoPlayer);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.TicksSinceSpot.Value).IsEqualTo(0);
    }

    /// <summary>
    ///     The trade window keeps running on the EVENT tick, because its anchor does:
    ///     <c>LastDeathTick</c> is written by this same edge off the death event's own stamp, so
    ///     window and anchor are already the same instant and moving one of them would introduce
    ///     the mismatch the spot interval above exists to remove. Pinned on the exact boundary —
    ///     256 ticks on the event clock, 257 on the frame's — so a change of clock cannot pass.
    /// </summary>
    [Test]
    public async Task TradeWindow_StaysOnTheEventTick()
    {
        PlayerContextIndex index = TwoVsTwo();
        Fixture f = Build(index);

        // Slot 6 (CT) was killed by slot 1 (T) exactly one window ago on the event clock.
        index.RecordDeath(6, killerSlot: 1, assisterSlot: NoPlayer, tick: 743);

        // Slot 5 (CT) now kills slot 1, avenging their teammate.
        bool applied = Apply(f, victim: 1, killer: 5, assister: NoPlayer, eventTick: 999, frameTick: 1000);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.TradeKill.IsActive).IsTrue()
            .Because("999 - 743 is exactly the 256-tick window; the frame header's 1000 would be "
                     + "257 and would drop a trade the demo actually contains");
    }

    /// <summary>
    ///     The flash-kill window keeps running on the EVENT tick for the same reason: its anchor,
    ///     <c>BlindedAtTick</c>, is written by <c>BlindEnrichmentEdge</c> off the blind event's own
    ///     <c>GameTick</c>. Pinned on the 320-tick boundary.
    /// </summary>
    [Test]
    public async Task FlashKillWindow_StaysOnTheEventTick()
    {
        PlayerContextIndex index = TwoVsTwo();
        Fixture f = Build(index);

        // Slot 1 (T) was blinded by slot 6 (CT) exactly one window ago, then killed by slot 6's
        // teammate slot 5 (CT).
        index.RecordBlind(victimSlot: 1, flasherSlot: 6, tick: 679);

        bool applied = Apply(f, victim: 1, killer: 5, assister: NoPlayer, eventTick: 999, frameTick: 1000);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.FlashKill.IsActive).IsTrue()
            .Because("999 - 679 is exactly the 320-tick window; the frame header's 1000 would be "
                     + "321 and would drop the flash assist");
    }

    /// <summary>
    ///     A killer who has spotted nobody this round reports the sentinel, not a zero interval: a
    ///     bound is written as <c>ticks_since_spot &lt;= N</c>, and 0 would pass every one of them.
    /// </summary>
    [Test]
    public async Task KillWithNoContact_ReportsTheSentinel()
    {
        Fixture f = Build(TwoVsTwo());

        bool applied = Apply(f, victim: 1, killer: 5, assister: NoPlayer);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.TicksSinceSpot.Value).IsEqualTo(AimShotContextEdge.NoSpotSentinel);
    }

    /// <summary>Assister with an unknown team (never registered → team 0) must not classify as enemy.</summary>
    [Test]
    public async Task UnknownAssisterTeam_EnemyAssistDoesNotFire()
    {
        Fixture f = Build(TwoVsTwo());
        bool applied = Apply(f, victim: 1, killer: 5, assister: 9);

        await Assert.That(applied).IsTrue();
        await Assert.That(f.EnemyAssist.IsActive).IsFalse()
            .Because("both assister and victim teams must be known (> 1) to assert enmity");
    }
}
