#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The <c>decoy_started</c> and <c>inferno_started</c> views on the reference demo: one
///     <c>match_event</c> row per wire event, carrying the event's entity id and position, and a
///     decoy's entity id is the entity index of a live decoy projectile row on that tick.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class GrenadeBurstViewTests
{
    private const string Rules = """
        ruleset: bursts
        for: match
        stats:
          decoy_entity:   { capture: event.EntityId,   on: decoy_started,   per: match }
          decoy_tick:     { capture: event.frame_tick, on: decoy_started,   per: match }
          decoy_x:        { capture: event.X,          on: decoy_started,   per: match }
          decoy_pawn:     { capture: event.UserId,     on: decoy_started,   per: match }
          inferno_entity: { capture: event.EntityId,   on: inferno_started, per: match }
          inferno_tick:   { capture: event.frame_tick, on: inferno_started, per: match }
          inferno_x:      { capture: event.X,          on: inferno_started, per: match }
        show:
          tables:
            decoys:
              per: match_event
              on: decoy_started
              columns:
                - { stat: decoy_entity, label: entity }
                - { stat: decoy_tick, label: burst_tick }
                - { stat: decoy_x, label: x }
                - { stat: decoy_pawn, label: pawn }
            infernos:
              per: match_event
              on: inferno_started
              columns:
                - { stat: inferno_entity, label: entity }
                - { stat: inferno_tick, label: burst_tick }
                - { stat: inferno_x, label: x }
        ---
        ruleset: grenades
        for: each_projectile
        stats:
          class: { capture: projectile.class, keep: first }
        show:
          tables:
            grenades:
              per: projectile
              columns:
                - { stat: class, label: class }
        """;

    [Test]
    public async Task BurstViews_CarryTheWireEvents_AndDecoysJoinTheirProjectileRow()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.ReferenceDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        List<(int Tick, int Entity, float X)> decoys = [], infernos = [];
        foreach (GameEvent ev in demo.AllGameEvents)
        {
            switch (ev.Payload)
            {
                case DecoyStartedEvent d:
                    decoys.Add((ev.GameTick, d.EntityId, d.X));
                    break;
                case InfernoStartburnEvent i:
                    infernos.Add((ev.GameTick, i.EntityId, i.X));
                    break;
            }
        }

        Console.WriteLine($"wire: {decoys.Count} decoy_started, {infernos.Count} inferno_startburn");
        await Assert.That(decoys.Count).IsGreaterThan(0);
        await Assert.That(infernos.Count).IsGreaterThan(0);

        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(Rules), new AnalysisOptions { CaptureSnapshots = false });
        await Assert.That(run.Build.ExcludedRulesets).IsEmpty()
            .Because(string.Join("; ", run.Build.ExcludedRulesets.SelectMany(e => e.Diagnostics)));

        MetricTable decoyTable = RoundFactsTestSupport.Table(run, "decoys", demo);
        MetricTable infernoTable = RoundFactsTestSupport.Table(run, "infernos", demo);
        await Assert.That(Rows(decoyTable)).IsEquivalentTo(decoys);
        await Assert.That(Rows(infernoTable)).IsEquivalentTo(infernos);

        // A decoy_started names the decoy projectile: a live decoy row on that tick has its index.
        MetricTable grenades = RoundFactsTestSupport.Table(run, "grenades", demo);
        int joined = 0;
        foreach (MetricRow row in decoyTable.Rows)
        {
            int entity = RoundFactsTestSupport.Int(row, "entity")!.Value;
            int tick = RoundFactsTestSupport.Int(row, "burst_tick")!.Value;
            bool live = grenades.Rows.Any(g =>
                RoundFactsTestSupport.Dim(g, "entity_index") == entity
                && Equals(g.Values["class"], "decoy")
                && RoundFactsTestSupport.Dim(g, "first_seen_tick") <= tick
                && tick <= RoundFactsTestSupport.Dim(g, "removed_tick"));
            await Assert.That(live).IsTrue().Because($"decoy entity {entity} at tick {tick}");
            await Assert.That(RoundFactsTestSupport.Int(row, "pawn")).IsNotEqualTo(0).Because($"decoy entity {entity} at tick {tick}");
            joined++;
        }

        Console.WriteLine($"{joined} decoys joined to a projectile row");
        await Assert.That(joined).IsEqualTo(decoys.Count);
    }

    private static List<(int Tick, int Entity, float X)> Rows(MetricTable table) =>
        table.Rows.Select(r => (
            RoundFactsTestSupport.Int(r, "burst_tick")!.Value,
            RoundFactsTestSupport.Int(r, "entity")!.Value,
            (float)Convert.ToDouble(r.Values["x"], CultureInfo.InvariantCulture))).ToList();
}
