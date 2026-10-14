#region

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     <c>player.pos_x@-8</c> resolves to a look-back entity read at the fire-time slots, hashes
///     apart from the plain read, and is refused everywhere the scanner keeps no history.
/// </summary>
[Category("Unit")]
public class LookBackResolveTests
{
    private const string Header = """
        ruleset: lb
        for: each_player
        cadences:
          step: { ticks: 8, anchor: round_freeze_end }
        stats:
        """;

    private static RulesetResolveResult Resolve(string stats) => RoundFactsTestSupport.Resolve(Header + "\n" + stats);

    private static IEnumerable<string> Codes(RulesetResolveResult result) => result.Diagnostics.Select(d => d.Code);

    [Test]
    public async Task ValueAndWhere_LookBack_IsAnEntityReadWithItsTicks()
    {
        RulesetResolveResult result = Resolve("""
              then:
                capture: player.pos_x@-8
                on: cadence.step
                where: player.pos_x@-16 != player.pos_x
                per: round
            """);
        CheckedStat stat = result.Ruleset!.Stats.Single();

        string[] reads = stat.EntityReads.Select(r => $"{r.Path}|{r.ProviderName}|{r.LookBackTicks}")
            .OrderBy(r => r, StringComparer.Ordinal).ToArray();
        await Assert.That(string.Join(",", reads)).IsEqualTo(
            "player.pos_x|entity.pawn.pos_x|0,player.pos_x|entity.pawn.pos_x|16,player.pos_x|entity.pawn.pos_x|8");
        await Assert.That(V1ExpressionWriter.Write(stat.ValueSelector!.Root)).IsEqualTo("player.pos_x@-8");
    }

    [Test]
    public async Task LookBack_HashesApartFromThePlainRead()
    {
        RulesetResolveResult result = Resolve("""
              plain:
                capture: player.pos_x
                on: cadence.step
                per: round
              back8:
                capture: player.pos_x@-8
                on: cadence.step
                per: round
              back16:
                capture: player.pos_x@-16
                on: cadence.step
                per: round
            """);
        string[] hashes = result.Ruleset!.Stats
            .Select(s => Convert.ToHexString(V2StatHasher.Hash(s, new PathHashes()))).ToArray();

        await Assert.That(hashes.Distinct().Count()).IsEqualTo(3);
    }

    [Test]
    public async Task LookBack_OutsideAFireTimeSlot_IsRefused()
    {
        RulesetResolveResult compute = Resolve("""
              moved:
                compute: player.pos_x@-8
                per: round
            """);
        await Assert.That(Codes(compute)).Contains(ResolveDiagnosticCodes.LookBackUnsupported);

        RulesetResolveResult whileGate = Resolve("""
              gated:
                count: cadence.step
                while: player.pos_x@-8 > 0
                per: round
            """);
        await Assert.That(Codes(whileGate)).Contains(ResolveDiagnosticCodes.LookBackUnsupported);
    }

    [Test]
    public async Task LookBack_OnAnythingButAPlayerEntityRead_IsRefused()
    {
        foreach (string read in new[] { "event.tick@-8", "player.cmd.tick@-8", "match.freeze_period@-8" })
        {
            RulesetResolveResult result = Resolve($"""
                  x:
                    capture: {read}
                    on: cadence.step
                    per: round
                """);
            await Assert.That(Codes(result)).Contains(ResolveDiagnosticCodes.LookBackUnsupported).Because(read);
        }

        RulesetResolveResult role = Resolve("""
              victim_hp:
                capture: victim.health@-8
                on: kill
                per: round
            """);
        await Assert.That(Codes(role)).Contains(ResolveDiagnosticCodes.LookBackUnsupported);

        RulesetResolveResult stat = Resolve("""
              kills:
                count: kill
                per: round
              earlier:
                capture: kills@-8
                on: cadence.step
                per: round
            """);
        await Assert.That(Codes(stat)).Contains(DiagnosticCodes.LookBackStat);
    }

    private sealed class PathHashes : IStatHashSource
    {
        public ReadOnlyMemory<byte> GetStatHash(ResolvedReference reference) =>
            SHA256.HashData(Encoding.UTF8.GetBytes(reference.StatPath!));
    }
}

/// <summary>
///     On the sample, <c>player.pos_x@-8</c> at a cadence step reads what the plain
///     <c>player.pos_x</c> capture read at the step whose snapshot is eight ticks older, on both paths.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class LookBackCorpusTests
{
    private const string Rules = """
        ruleset: lb_demo
        for: each_player
        cadences:
          step: { ticks: 8, anchor: round_freeze_end, until: round_ended }
        stats:
          now_x:
            capture: player.pos_x
            on: cadence.step
            per: match
          back_x:
            capture: player.pos_x@-8
            on: cadence.step
            per: match
        show:
          tables:
            steps:
              per: player_event
              on: cadence.step
              columns:
                - { stat: now_x, label: now_x }
                - { stat: back_x, label: back_x }
        """;

    [Test]
    public async Task LookBack_AtACadenceStep_EqualsTheCaptureEightTicksEarlier()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = RoundFactsTestSupport.Sample();

        // A step's reads see the snapshot folded through the frame before the one the step fires on.
        Dictionary<int, int> snapshotTickAtFire = [];
        int previous = int.MinValue;
        foreach (DemoFrame frame in demo.Frames)
        {
            int tick = frame.GameTick ?? frame.ServerTick;
            if (tick != previous)
            {
                snapshotTickAtFire.TryAdd(tick, previous);
            }

            previous = tick;
        }

        foreach ((string label, AnalysisRun run) in RoundFactsCorpusTests.RunBothPaths(path, RoundFactsTestSupport.Load(Rules)))
        {
            await Assert.That(run.Build.ExcludedRulesets).IsEmpty()
                .Because($"{label}: {string.Join("; ", run.Build.RulesetDiagnostics)}");

            Dictionary<(int Slot, int Snapshot), double> nowAt = [];
            List<(int Slot, int Tick, double Back, double Now)> rows = [];
            foreach (MetricRow row in RoundFactsTestSupport.Table(run, "steps", demo).Rows)
            {
                int slot = Convert.ToInt32(row.Dimensions["player_slot"], CultureInfo.InvariantCulture);
                int tick = Convert.ToInt32(row.Dimensions["tick"], CultureInfo.InvariantCulture);
                double now = Convert.ToDouble(row.Values["now_x"], CultureInfo.InvariantCulture);
                double back = Convert.ToDouble(row.Values["back_x"], CultureInfo.InvariantCulture);
                nowAt[(slot, snapshotTickAtFire[tick])] = now;
                rows.Add((slot, tick, back, now));
            }

            int compared = 0, moved = 0;
            foreach ((int slot, int tick, double back, double now) in rows)
            {
                if (!nowAt.TryGetValue((slot, snapshotTickAtFire[tick] - 8), out double earlier))
                {
                    continue;
                }

                await Assert.That(back).IsEqualTo(earlier).Because($"{label} slot {slot} step at tick {tick}");
                compared++;
                moved += back != now ? 1 : 0;
            }

            Console.WriteLine($"{label}: {rows.Count} rows, {compared} look-backs compared, {moved} differ from the plain read");
            await Assert.That(compared).IsGreaterThan(1000).Because(label);
            await Assert.That(moved).IsGreaterThan(compared / 10).Because(label);
        }
    }
}
