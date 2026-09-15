#region

using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The two paths must agree: the same graph evaluated over a forward reader that drops frames
///     as it goes, and over the retained frame list with the up-front parallel digest, produce the
///     same timeline, highlights, node values and configured tables. Player names are compared by
///     slot: the reader resolves a name when the slot first materialises, the list resolves the
///     final one, and a mid-match rename is the one place the two legitimately read differently.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class ForwardPathParityTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Evaluate_OverReader_MatchesEvaluate_OverParsedDemo(bool captureSnapshots)
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
        await Assert.That(list.Provenance.Digest).IsEqualTo(DigestProducerKind.ParallelUpFront);
        await Assert.That(streamed.Provenance.Digest).IsEqualTo(DigestProducerKind.Sequential);
        await Assert.That(streamed.Provenance.FramesConsumed).IsEqualTo(list.Provenance.FramesConsumed);
        await Assert.That(streamed.Provenance.FramesConsumed).IsEqualTo(demo.Frames.Count);
        await Assert.That(streamed.Provenance.MessagesConsumed).IsEqualTo(list.Provenance.MessagesConsumed);
        await Assert.That(streamed.Provenance.SnapshotsCaptured).IsEqualTo(captureSnapshots);

        string expected = RunDigest.Render(list);
        string actual = RunDigest.Render(streamed);
        await Assert.That(actual).IsEqualTo(expected);
        await Assert.That(expected).Contains("[nodes]");
        await Assert.That(streamed.Demo).IsEqualTo(list.Demo with { Players = streamed.Demo.Players });
        await Assert.That(streamed.Demo.Players.Count).IsEqualTo(list.Demo.Players.Count);
    }

    /// <summary>
    ///     Renders everything an <see cref="AnalysisRun" /> carries in both capture modes, every
    ///     section ordinally sorted, with each run's own materialised player names replaced by a slot
    ///     token so a rename does not read as a value change.
    /// </summary>
    internal static class RunDigest
    {
        public static string Render(AnalysisRun run)
        {
            List<(string Name, string Token)> names = run.MaterializedPlayers
                .Where(mp => !string.IsNullOrEmpty(mp.PlayerName))
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

            if (run.Snapshots is not null)
            {
                sb.Append("[tables]\n");
                foreach (MetricTable t in run.ProjectConfiguredOutputs().OrderBy(t => t.Name, StringComparer.Ordinal))
                {
                    sb.Append("== ").Append(t.Name).Append('\n');
                    sb.Append("dims=").Append(string.Join(",", t.DimensionColumns)).Append('\n');
                    sb.Append("vals=").Append(string.Join(",", t.ValueColumns)).Append('\n');
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
