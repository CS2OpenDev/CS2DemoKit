#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The two paths must agree: the same rules run over a forward reader that decodes only what
///     the graph asked for and drops frames as it goes, and over the retained frame list, through
///     the one pipelined producer, produce the same timeline, highlights, node values, materialised
///     roster, per-slot teams and configured tables. Player names are compared by slot: the reader
///     resolves a name when the slot first materialises, the list resolves the final one, and a
///     mid-match rename is the one place the two legitimately read differently.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ForwardPathParityTests
{
    public static IEnumerable<string> CorpusDemos() => RulesOutputGoldenTests.CorpusDemos();

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Evaluate_OverAnUnnarrowedReader_MatchesEvaluate_OverParsedDemo(bool captureSnapshots)
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();

        BuildResult listBuild = DemoAnalysis.Build(demo, rules.Rulesets);
        AnalysisRun list = DemoAnalysis.Evaluate(demo, listBuild, new AnalysisOptions { CaptureSnapshots = captureSnapshots });

        BuildResult readerBuild = DemoAnalysis.Build(demo, rules.Rulesets);
        AnalysisRun streamed;
        using (DemoReader reader = DemoReader.Open(bytes.AsMemory()))
        {
            streamed = DemoAnalysis.Evaluate(reader, readerBuild, new AnalysisOptions { CaptureSnapshots = captureSnapshots });
        }

        await Assert.That(list.Provenance.Source).IsEqualTo(AnalysisSourceKind.Materialised);
        await Assert.That(streamed.Provenance.Source).IsEqualTo(AnalysisSourceKind.Stream);
        await Assert.That(list.Provenance.Digest).IsEqualTo(DigestProducerKind.Pipelined);
        await Assert.That(streamed.Provenance.Digest).IsEqualTo(DigestProducerKind.Pipelined);
        await Assert.That(streamed.Provenance.FramesConsumed).IsEqualTo(list.Provenance.FramesConsumed);
        await Assert.That(streamed.Provenance.FramesConsumed).IsEqualTo(demo.Frames.Count);
        // A stream releases a frame's entity payload once folded, so those are never dispatched.
        await Assert.That(streamed.Provenance.MessagesConsumed).IsLessThan(list.Provenance.MessagesConsumed);
        await Assert.That(streamed.Provenance.SnapshotsCaptured).IsEqualTo(captureSnapshots);
        await Assert.That(streamed.Provenance.DecodePlan!.DecodesEverything).IsTrue();

        await AssertSameRun(list, streamed);
        await Assert.That(streamed.Demo).IsEqualTo(list.Demo with { Players = streamed.Demo.Players });
        await Assert.That(streamed.Demo.Players.Count).IsEqualTo(list.Demo.Players.Count);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Run_OverAFile_MatchesRun_OverParsedDemo(bool captureSnapshots)
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        AnalysisOptions options = new() { CaptureSnapshots = captureSnapshots };

        AnalysisRun list = DemoAnalysis.Run(demo, rules.Rulesets, options);
        AnalysisRun streamed = DemoAnalysis.Run(path, rules.Rulesets, options);

        await Assert.That(streamed.Provenance.Source).IsEqualTo(AnalysisSourceKind.Stream);
        await Assert.That(streamed.Provenance.Digest).IsEqualTo(DigestProducerKind.Pipelined);
        await Assert.That(streamed.Provenance.SnapshotsCaptured).IsEqualTo(captureSnapshots);
        await Assert.That(streamed.Provenance.FramesConsumed).IsEqualTo(list.Provenance.FramesConsumed);
        await Assert.That(streamed.Provenance.ProfileResolution).IsEqualTo(ProfileResolutionKind.HeaderAndVocabulary);
        await Assert.That(streamed.Provenance.Profile.GetType()).IsEqualTo(list.Provenance.Profile.GetType());

        // The narrowed plan is what the file path is for: no user commands, not everything.
        DecodePlan plan = streamed.Provenance.DecodePlan!;
        await Assert.That(plan.DecodesEverything).IsFalse();
        await Assert.That(plan.Decodes(NetMessageCatalog.UserCmdsTypeId)).IsFalse();
        await Assert.That(plan.Categories.HasFlag(MessageCategories.Entities)).IsTrue();
        await Assert.That(streamed.Provenance.MessagesConsumed).IsLessThan(list.Provenance.MessagesConsumed);

        await AssertSameRun(list, streamed);
    }

    /// <summary>
    ///     The pipelined producer and the sequential one must agree to the byte, names included: the
    ///     pipeline pins the roster view to the frame the evaluator has read or peeked, exactly as
    ///     the sequential reader exposes it, so a materialisation reads the same name on both.
    /// </summary>
    [Test]
    public async Task Run_Pipelined_MatchesRun_Sequential_NamesIncluded()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();

        AnalysisRun pipelined = DemoAnalysis.Run(path, rules.Rulesets);
        AnalysisRun sequential = DemoAnalysis.Run(path, rules.Rulesets, new AnalysisOptions { MaxDegreeOfParallelism = 1 });

        await Assert.That(pipelined.Provenance.Digest).IsEqualTo(DigestProducerKind.Pipelined);
        await Assert.That(sequential.Provenance.Digest).IsEqualTo(DigestProducerKind.Sequential);
        await Assert.That(pipelined.Provenance.DecodePlan).IsNotNull();
        await Assert.That(pipelined.Provenance.FramesConsumed).IsEqualTo(sequential.Provenance.FramesConsumed);
        await Assert.That(pipelined.Provenance.MessagesConsumed).IsEqualTo(sequential.Provenance.MessagesConsumed);
        await Assert.That(RunDigest.Render(pipelined, false)).IsEqualTo(RunDigest.Render(sequential, false));
        await Assert.That(pipelined.MaterializedPlayers.Select(mp => mp.PlayerName).ToList())
            .IsEquivalentTo(sequential.MaterializedPlayers.Select(mp => mp.PlayerName).ToList());
    }

    [Test]
    public async Task Run_DefaultsSnapshotsOffOverAStream_AndOnOverAParsedDemo()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();

        AnalysisRun streamed = DemoAnalysis.Run(path, rules.Rulesets);
        await Assert.That(streamed.Snapshots).IsNull();
        await Assert.That(streamed.Provenance.SnapshotsCaptured).IsFalse();
        await Assert.That(streamed.MaterializedPlayers.Count).IsGreaterThan(0);

        AnalysisRun list = DemoAnalysis.Run(demo, rules.Rulesets);
        await Assert.That(list.Snapshots).IsNotNull();
        await Assert.That(list.Provenance.SnapshotsCaptured).IsTrue();

        byte[] bytes = await File.ReadAllBytesAsync(path);
        AnalysisRun fromBytes = DemoAnalysis.Run(bytes.AsMemory(), rules.Rulesets);
        await Assert.That(RunDigest.Render(fromBytes)).IsEqualTo(RunDigest.Render(streamed));
    }

    /// <summary>
    ///     A run without snapshots projects its configured tables from what it sampled at the round
    ///     boundaries, and they must be the tables a snapshot run projects: every row, dimension, value
    ///     and column clock. Covered with the shipped rulesets plus a per-side and a match ruleset, over
    ///     a retained demo and over a forward reader.
    /// </summary>
    [Test]
    public async Task ConfiguredTables_WithoutSnapshots_MatchTheSnapshotProjection()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        await AssertTablesAgree(path, demo);
    }

    /// <summary>The same agreement over whatever demos the corpus holds.</summary>
    [Test]
    [MethodDataSource(nameof(CorpusDemos))]
    public async Task Corpus_ConfiguredTables_WithoutSnapshots_MatchTheSnapshotProjection(string demoPath)
    {
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        await AssertTablesAgree(demoPath, demo);
    }

    private const string TableRulesets = """
        ruleset: parity_sides
        for: each_team
        stats:
          kills:
            count: kill
            per: round
          won:
            count: round_won
            per: round
          decided_at:
            capture: event.frame_tick
            on: round_decided
            per: round
          money:
            capture: round.team.money
            on: raw.round_freeze_end
            per: round
          match_kills:
            count: kill
            per: match
        show:
          tables:
            parity_sides_round:
              per: team_round
              columns:
                - { stat: kills, label: K }
                - { stat: won, label: W }
                - { stat: decided_at, label: Decided }
                - { stat: money, label: M }
            parity_sides_match:
              per: team_match
              columns:
                - { stat: match_kills, label: K }
        ---
        ruleset: parity_match
        for: match
        stats:
          kills:
            count: kill
            per: match
          plant_at:
            capture: event.tick
            on: bomb_planted
            keep: list
            per: match
        show:
          tables:
            parity_match:
              per: match
              columns:
                - { stat: kills, label: K }
                - { stat: plant_at, label: Plants }
        """;

    private static async Task AssertTablesAgree(string path, ParsedDemo demo)
    {
        RuleConfigLoadResult shipped = YamlConfigLoader.LoadShippedEmbedded();
        RuleConfigLoadResult extra = YamlConfigLoader.LoadDocuments([("parity.rules.yaml", TableRulesets)]);
        await Assert.That(extra.Errors.Count).IsEqualTo(0);
        List<RulesetsV2.Model.RulesetDoc> rules = [.. shipped.Rulesets, .. extra.Rulesets];

        AnalysisRun snapshots = DemoAnalysis.Run(demo, rules, new AnalysisOptions { CaptureSnapshots = true });
        AnalysisRun bare = DemoAnalysis.Run(demo, rules, new AnalysisOptions { CaptureSnapshots = false });
        AnalysisRun streamed = DemoAnalysis.Run(path, rules);

        await Assert.That(snapshots.Build.RulesetDiagnostics.Count).IsEqualTo(0);
        await Assert.That(bare.Snapshots).IsNull();
        await Assert.That(streamed.Snapshots).IsNull();

        string expected = RunDigest.Render(snapshots);
        await Assert.That(expected).Contains("== parity_sides_round");
        await Assert.That(RunDigest.Render(bare)).IsEqualTo(expected);
        await Assert.That(RunDigest.Render(streamed)).IsEqualTo(expected);
    }

    [Test]
    [Explicit]
    [MethodDataSource(nameof(CorpusDemos))]
    public async Task Corpus_RunOverAFile_MatchesRun_OverParsedDemo(string demoPath)
    {
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        AnalysisOptions options = new() { CaptureSnapshots = true };

        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        AnalysisRun list = DemoAnalysis.Run(demo, rules.Rulesets, options);
        AnalysisRun streamed = DemoAnalysis.Run(demoPath, rules.Rulesets, options);

        await Assert.That(streamed.Provenance.FramesConsumed).IsEqualTo(list.Provenance.FramesConsumed);
        await Assert.That(streamed.Provenance.Profile.GetType()).IsEqualTo(list.Provenance.Profile.GetType());
        await AssertSameRun(list, streamed);
    }

    private static async Task AssertSameRun(AnalysisRun list, AnalysisRun streamed)
    {
        string expected = RunDigest.Render(list);
        string actual = RunDigest.Render(streamed);
        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(expected).Contains("[nodes]");

        // Materialisation order and the team each slot ended on, both seeded from entity state.
        await Assert.That(streamed.MaterializedPlayers.Select(mp => mp.PlayerSlot).ToList())
            .IsEquivalentTo(list.MaterializedPlayers.Select(mp => mp.PlayerSlot).ToList());
        await Assert.That(TeamsBySlot(streamed.Build)).IsEqualTo(TeamsBySlot(list.Build));
    }

    private static string TeamsBySlot(BuildResult build)
    {
        StringBuilder sb = new();
        for (int slot = 0; slot < 64; slot++)
        {
            if (build.PlayerContextIndex!.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx))
            {
                sb.Append(slot).Append(':').Append(ctx!.Team).Append(' ');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    ///     Renders everything an <see cref="AnalysisRun" /> carries in both capture modes, every
    ///     section ordinally sorted, with each run's own materialised player names replaced by a slot
    ///     token so a rename does not read as a value change.
    /// </summary>
    internal static class RunDigest
    {
        public static string Render(AnalysisRun run, bool normaliseNames = true)
        {
            List<(string Name, string Token)> names = run.MaterializedPlayers
                .Where(mp => normaliseNames && !string.IsNullOrEmpty(mp.PlayerName))
                .Select(mp => (mp.PlayerName, $"<slot{mp.PlayerSlot}>"))
                .OrderByDescending(n => n.PlayerName.Length)
                .ToList();

            string Normalise(string? text)
            {
                if (string.IsNullOrEmpty(text))
                {
                    return "-";
                }

                foreach ((string name, string token) in names)
                {
                    text = text.Replace(name, token, StringComparison.Ordinal);
                }

                return text;
            }

            StringBuilder sb = new();
            sb.Append("highlights=").Append(run.Highlights.Count).Append('\n');
            sb.Append("ruleChainEvents=").Append(run.Timeline.Events.Count).Append('\n');
            sb.Append("materializedPlayers=").Append(run.MaterializedPlayers.Count).Append('\n');
            sb.Append("materializedSlots=")
                .Append(string.Join(",", run.MaterializedPlayers.Select(mp => mp.PlayerSlot).Order())).Append('\n');

            sb.Append("[chains]\n");
            foreach (string line in run.Timeline.Events
                         .Select(e => $"{e.ChainName}|{e.Tick}|{e.FrameIndex}|{e.PlayerSlot?.ToString(CultureInfo.InvariantCulture) ?? "-"}|{Normalise(e.PlayerName)}")
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(line).Append('\n');
            }

            sb.Append("[highlights]\n");
            foreach (string line in run.Highlights
                         .Select(h => $"{h.RulesetId}/{h.HighlightId}|{h.Tick}|{h.PlayerSlot}|{h.RoundNumber}|{Normalise(h.RenderedTitle)}")
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(line).Append('\n');
            }

            sb.Append("[nodes]\n");
            foreach (string line in run.FinalNodes
                         .Select(n => $"{n.Name}|{Normalise(n.Subtitle)}|{n.IsActive}|{Normalise(n.GetDisplayValue())}|" +
                                      $"{(n.GetNumericValue() is { } f ? f.ToString("F6", CultureInfo.InvariantCulture) : "-")}")
                         .OrderBy(x => x, StringComparer.Ordinal))
            {
                sb.Append(line).Append('\n');
            }

            // Both capture modes project: a snapshot run from its rows, a run without snapshots from
            // what it recorded at the round boundaries.
            {
                sb.Append("[tables]\n");
                foreach (MetricTable t in run.ProjectConfiguredOutputs().OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    sb.Append("== ").Append(t.Name).Append('\n');
                    sb.Append("dims=").Append(string.Join(",", t.DimensionColumns)).Append('\n');
                    sb.Append("vals=").Append(string.Join(",", t.ValueColumns)).Append('\n');
                    sb.Append("clocks=").Append(string.Join(",", t.ColumnClocks.OrderBy(c => c.Key, StringComparer.Ordinal)
                        .Select(c => $"{c.Key}:{c.Value}"))).Append('\n');
                    foreach (string line in t.Rows
                                 .Select(r =>
                                     string.Join("|", t.DimensionColumns.Select(c => Normalise(Fmt(r.Dimensions.GetValueOrDefault(c))))) +
                                     "->" +
                                     string.Join("|", t.ValueColumns.Select(c => Fmt(r.Values.GetValueOrDefault(c)))))
                                 .OrderBy(x => x, StringComparer.Ordinal))
                    {
                        sb.Append(line).Append('\n');
                    }
                }
            }

            return sb.ToString();
        }

        private static string Fmt(object? value) => value switch
        {
            null => "-",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "-"
        };
    }
}
