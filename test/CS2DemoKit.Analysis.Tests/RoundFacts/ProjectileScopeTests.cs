#region

using System.Globalization;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>for: each_projectile</c> on the sample demo, both paths: one <c>grenades</c> row per
///     <c>projectile_created</c>, keyed by entity index and serial, with the thrower, the class, the
///     position on the frame the instance materialized and the one it was removed on, the bounce
///     count as last seen, a thrower read at creation, and a capture and a count on the landing. The
///     parser's <see cref="ProjectileSampler" /> is the oracle for the positions.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class ProjectileScopeTests
{
    private const string GrenadeRows = """
        ruleset: grenade_rows
        for: each_projectile
        stats:
          class:        { capture: projectile.class,        keep: first }
          thrower:      { capture: projectile.thrower,      keep: first }
          spawn_x:      { capture: projectile.pos_x,        keep: first }
          initial_x:    { capture: projectile.initial_x,    keep: first }
          end_x:        { capture: projectile.pos_x,        keep: last }
          bounces:      { capture: projectile.bounces,      keep: last }
          thrower_walk: { capture: thrower.is_walking,      on: raw.projectile_created }
          landed_tick:  { capture: event.frame_tick,        on: projectile_landed }
          landed_x:     { capture: event.X,                 on: projectile_landed }
          lands:        { count: projectile_landed }
          births:       { count: raw.projectile_created }
        show:
          tables:
            grenades:
              per: projectile
              columns:
                - { stat: class, label: class }
                - { stat: thrower, label: thrower }
                - { stat: spawn_x, label: spawn_x }
                - { stat: initial_x, label: initial_x }
                - { stat: end_x, label: end_x }
                - { stat: bounces, label: bounces }
                - { stat: thrower_walk, label: thrower_walk }
                - { stat: landed_tick, label: landed_tick }
                - { stat: landed_x, label: landed_x }
                - { stat: lands, label: lands }
                - { stat: births, label: births }
        """;

    [Test]
    public async Task OneRowPerCreatedProjectile_OnBothPaths()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        List<(int FrameIndex, int ServerTick, Parser.GameEvents.GameEvent Event)> scanned =
            EntityIntegrationTests.ScanProjectileEvents(demo);
        List<(int FrameIndex, ProjectileCreatedEvent E)> created = [.. scanned
            .Where(e => e.Event is ProjectileCreatedEvent)
            .Select(e => (e.FrameIndex, (ProjectileCreatedEvent)e.Event))];
        Dictionary<(int, int), ProjectileRemovedEvent> removed = scanned
            .Where(e => e.Event is ProjectileRemovedEvent)
            .Select(e => (ProjectileRemovedEvent)e.Event)
            .ToDictionary(e => (e.EntityIndex, e.Serial));
        List<ProjectileSample> samples = [.. ProjectileSampler.Walk(demo)];

        await Assert.That(created.Count).IsGreaterThan(0);

        string? previous = null;
        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path,
                     RoundFactsTestSupport.Load(GrenadeRows)))
        {
            await Assert.That(run.Build.ExcludedRulesets.Count).IsEqualTo(0)
                .Because($"{label}: {string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics).Select(d => d.ToString()))}");
            await Assert.That(run.Build.RulesetDiagnostics.Count).IsEqualTo(0)
                .Because($"{label}: {string.Join("; ", run.Build.RulesetDiagnostics.Select(d => d.ToString()))}");
            MetricTable table = RoundFactsTestSupport.Table(run, "grenades", demo);
            await Assert.That(table.Rows.Count).IsEqualTo(created.Count).Because(label);
            await Assert.That(table.ColumnClocks.GetValueOrDefault("created_tick")).IsEqualTo(MetricTable.FrameClock).Because(label);
            await Assert.That(table.ColumnClocks.GetValueOrDefault("removed_tick")).IsEqualTo(MetricTable.FrameClock).Because(label);

            HashSet<(int, int)> seen = [];
            foreach (MetricRow row in table.Rows)
            {
                int index = RoundFactsTestSupport.Dim(row, "entity_index");
                int serial = RoundFactsTestSupport.Dim(row, "serial");
                string because = $"{label}: projectile {index}:{serial}";
                await Assert.That(seen.Add((index, serial))).IsTrue().Because(because);

                (int createdFrame, ProjectileCreatedEvent birth) = created.Single(c => c.E.EntityIndex == index && c.E.Serial == serial);
                await Assert.That(RoundFactsTestSupport.Dim(row, "created_tick")).IsEqualTo(birth.GameTick).Because(because);
                await Assert.That(RoundFactsTestSupport.Dim(row, "player_slot")).IsEqualTo(birth.PlayerSlot).Because(because);
                await Assert.That(row.Values["class"]).IsEqualTo(birth.Grenade).Because(because);
                await Assert.That(RoundFactsTestSupport.Int(row, "thrower")).IsEqualTo(birth.PlayerSlot).Because(because);
                await Assert.That(RoundFactsTestSupport.Int(row, "births")).IsEqualTo(1).Because(because);
                await Assert.That(RoundFactsTestSupport.Int(row, "thrower_walk")).IsNotNull().Because(because);

                // The first-kept position is the row on the frame the instance materialized, which
                // the sampler also saw.
                ProjectileSample atBirth = samples.Single(s => s.EntityIndex == index && s.Serial == serial && s.FrameIndex == createdFrame);
                AssertFloat(row, "spawn_x", atBirth.Position?.X, because);
                AssertFloat(row, "initial_x", atBirth.InitialPosition?.X, because);

                if (removed.TryGetValue((index, serial), out ProjectileRemovedEvent? death))
                {
                    await Assert.That(RoundFactsTestSupport.Dim(row, "removed_tick")).IsEqualTo(death.GameTick).Because(because);
                    await Assert.That(RoundFactsTestSupport.Int(row, "lands")).IsEqualTo(1).Because(because);
                    await Assert.That(RoundFactsTestSupport.Int(row, "landed_tick")).IsEqualTo(death.GameTick).Because(because);
                    await Assert.That(RoundFactsTestSupport.Int(row, "bounces")).IsEqualTo(death.Bounces).Because(because);
                    AssertFloat(row, "end_x", float.IsNaN(death.X) ? null : death.X, because);
                    AssertFloat(row, "landed_x", float.IsNaN(death.X) ? null : death.X, because);
                }
                else
                {
                    await Assert.That(RoundFactsTestSupport.Int(row, "lands")).IsEqualTo(0).Because(because);
                }
            }

            string rendered = Render(table);
            if (previous is not null)
            {
                await Assert.That(rendered).IsEqualTo(previous).Because("the two paths project the same rows");
            }

            previous = rendered;
        }
    }

    private static void AssertFloat(MetricRow row, string column, float? expected, string because)
    {
        object? value = row.Values.GetValueOrDefault(column);
        if (expected is null)
        {
            if (value is double d && double.IsNaN(d))
            {
                return;
            }

            if (value is not null)
            {
                throw new InvalidOperationException($"{because}: {column} should be unreadable, was {value}");
            }

            return;
        }

        double actual = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s => double.Parse(s, CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException($"{because}: {column} is {value?.GetType().Name ?? "null"}")
        };
        if (Math.Abs(actual - expected.Value) > 0.01)
        {
            throw new InvalidOperationException($"{because}: {column} was {actual}, expected {expected}");
        }
    }

    private static string Render(MetricTable table) =>
        string.Join("\n", table.Rows.Select(r =>
            string.Join("|", r.Dimensions.Where(d => d.Key != "match_id").Select(d => $"{d.Key}={d.Value}"))
            + "||" + string.Join("|", r.Values.Select(v => $"{v.Key}={FormattableString.Invariant($"{v.Value}")}"))));
}
