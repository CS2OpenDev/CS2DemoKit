#region

using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>projectile_created</c> and <c>projectile_removed</c> reach rules as raw triggers and
///     through the <c>projectile_thrown</c> and <c>projectile_landed</c> views, whose <c>class</c>
///     facet and <c>event.*</c> fields are the filtered form. A subscription alone has to switch the
///     scanner's projectile rows on.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileEventRulesTests
{
    private const string Raw = """
        ruleset: projectiles
        for: match
        stats:
          thrown_at:
            capture: event.frame_tick
            on: raw.projectile_created
            keep: list
            per: match
          smoke_throwers:
            capture: event.PlayerSlot
            on: projectile_thrown
            match: { class: smoke }
            keep: list
            per: match
          landed_entities:
            capture: event.EntityIndex
            on: projectile_landed
            keep: list
            per: match
          removed_at:
            capture: event.frame_tick
            on: raw.projectile_removed
            keep: list
            per: match
        """;

    private const string Views = """
        ruleset: grenades
        for: each_player
        stats:
          thrown:
            count: projectile_thrown
            per: match
          smokes:
            count: projectile_thrown
            match: { class: smoke }
            per: match
          landed:
            count: projectile_landed
            per: match
        show:
          tables:
            grenades:
              per: player_match
              columns:
                - { stat: thrown, label: Thrown }
                - { stat: smokes, label: Smokes }
                - { stat: landed, label: Landed }
        """;

    [Test]
    public async Task Subscriptions_SwitchTheEventsOn_ThroughRawAndViews()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        List<(int FrameIndex, int ServerTick, Parser.GameEvents.GameEvent Event)> scanned =
            EntityIntegrationTests.ScanProjectileEvents(demo);
        int created = scanned.Count(e => e.Event is ProjectileCreatedEvent);
        int smokes = scanned.Count(e => e.Event is ProjectileCreatedEvent { ClassName: "CSmokeGrenadeProjectile" });
        int attributedRemovals = scanned.Count(e => e.Event is ProjectileRemovedEvent { PlayerSlot: >= 0 });
        int removals = scanned.Count(e => e.Event is ProjectileRemovedEvent);
        string landedEntities = string.Join(",", scanned
            .Where(e => e.Event is ProjectileRemovedEvent)
            .Select(e => ((ProjectileRemovedEvent)e.Event).EntityIndex));

        await Assert.That(smokes).IsGreaterThan(0);
        await Assert.That(smokes).IsLessThan(created);

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path,
                     RoundFactsTestSupport.Load(Raw, Views)))
        {
            await Assert.That(RoundFactsCorpusTests.Ints(run, "projectiles.thrown_at").Length).IsEqualTo(created).Because(label);
            await Assert.That(RoundFactsCorpusTests.Ints(run, "projectiles.smoke_throwers").Length).IsEqualTo(smokes).Because(label);
            await Assert.That(RoundFactsCorpusTests.Ints(run, "projectiles.removed_at").Length).IsEqualTo(removals).Because(label);
            await Assert.That(RoundFactsCorpusTests.List(run, "projectiles.landed_entities")).IsEqualTo(landedEntities).Because(label);

            MetricTable table = RoundFactsTestSupport.Table(run, "grenades", demo);
            await Assert.That(table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "Thrown") ?? 0)).IsEqualTo(created).Because(label);
            await Assert.That(table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "Smokes") ?? 0)).IsEqualTo(smokes).Because(label);
            await Assert.That(table.Rows.Sum(r => RoundFactsTestSupport.Int(r, "Landed") ?? 0)).IsEqualTo(attributedRemovals).Because(label);
        }
    }

    [Test]
    public async Task ARawTriggerAlone_SwitchesTheEventsOn()
    {
        const string rawOnly = """
            ruleset: raw_projectiles
            for: match
            stats:
              thrown_at:
                capture: event.frame_tick
                on: raw.projectile_created
                keep: list
                per: match
              removed_at:
                capture: event.frame_tick
                on: raw.projectile_removed
                keep: list
                per: match
            """;

        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        List<(int FrameIndex, int ServerTick, Parser.GameEvents.GameEvent Event)> scanned =
            EntityIntegrationTests.ScanProjectileEvents(RoundFactsTestSupport.Sample());
        string created = string.Join(",", scanned.Where(e => e.Event is ProjectileCreatedEvent).Select(e => e.ServerTick));
        string removed = string.Join(",", scanned.Where(e => e.Event is ProjectileRemovedEvent).Select(e => e.ServerTick));

        await Assert.That(created).IsNotEmpty();
        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, RoundFactsTestSupport.Load(rawOnly)))
        {
            await Assert.That(RoundFactsCorpusTests.List(run, "raw_projectiles.thrown_at")).IsEqualTo(created).Because(label);
            await Assert.That(RoundFactsCorpusTests.List(run, "raw_projectiles.removed_at")).IsEqualTo(removed).Because(label);
        }
    }
}
