#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Edges;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

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
        TransientBoolNode EnemyAssist);

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

        GenericBoolNode root = new("root");
        KillTeamEnrichmentEdge edge = new(
            root, index, enemyKill, teamKill, selfKill,
            tradeKill, tradedSlot, flashKill, flashSlot, enemyAssist);
        return new Fixture(edge, enemyKill, teamKill, selfKill, enemyAssist);
    }

    private static bool Apply(Fixture f, int victim, int killer, int assister, string weapon = "ak47")
    {
        GameEvent death = TestGameEvents.PlayerDeath(victim, killer, assister, weapon, dmgHealth: (short)100, gameTick: 100);
        GameEventMessage msg = GameEventMessage.ForSynthesizedEvent(death);
        DemoFrame frame = new()
        {
            Command = "DEM_Packet",
            FrameNumber = 0,
            ServerTick = 0,
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
