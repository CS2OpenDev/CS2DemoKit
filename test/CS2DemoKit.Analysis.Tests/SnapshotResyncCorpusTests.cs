#region

using System.Globalization;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Tests.RoundFacts;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The tournament demos each drop one snapshot, and the entity state after it must not depend on
///     how the decode was cut. The digest arm folds every frame with one chunk (the sequential
///     decode), with a chunk at every candidate full packet, and with a sparse plan, and holds each
///     frame's singletons and per-pawn fold equal; the run arm reads <c>team.score</c> at a
///     parallelism cap of one and at the default. Skips when the demo is not under
///     <c>CS2DEMOKIT_CORPUS_DIR</c>.
/// </summary>
[NotInParallel]
[Category("Corpus")]
public class SnapshotResyncCorpusTests
{
    // FreezePeriod first, then team.score T and CT, then the clan names.
    private const int ScoreT = 1;
    private const int ScoreCt = 2;

    private static IReadOnlyList<IEntityValueProvider> NewSingletons() =>
        [new FreezePeriodProvider(), .. BuiltinProviderSpecs.CreateTeamProviders()];

    /// <param name="file">A tournament demo under the corpus folder.</param>
    /// <param name="resyncFrame">The first full packet after the break, or -1 when the demo has none.</param>
    [Test]
    [Arguments("furia-vs-vitality-m1-mirage.dem", 249643)]
    [Arguments("furia-vs-vitality-m2-inferno.dem", -1)]
    [Arguments("furia-vs-vitality-m3-nuke.dem", 123087)]
    [Arguments("furia-vs-vitality-m4-overpass.dem", 217395)]
    [Arguments("vitality-vs-fut-m1-mirage.dem", -1)]
    [Arguments("vitality-vs-fut-m2-dust2.dem", 215127)]
    [Arguments("vitality-vs-fut-m3-nuke.dem", 176722)]
    public async Task EveryChunkPlan_FoldsTheSameState_AcrossTheDroppedSnapshot(string file, int resyncFrame)
    {
        string path = DemoTestHelper.RequireDemo(file);
        byte[] bytes = await File.ReadAllBytesAsync(path);

        EntityFrameDigest[] reference = Produce(bytes, int.MaxValue, out IReadOnlyList<PipelinedDigestSource.ChunkSpan> one);
        await Assert.That(one.Count).IsEqualTo(1).Because("an unbounded chunk is the sequential decode");

        if (resyncFrame >= 0)
        {
            object?[] after = reference[resyncFrame + 1].Singletons;
            Console.WriteLine($"{file}: after the full packet at frame {resyncFrame}: T {after[ScoreT]} CT {after[ScoreCt]}");
            await Assert.That(Math.Max(Convert.ToInt32(after[ScoreT], CultureInfo.InvariantCulture),
                Convert.ToInt32(after[ScoreCt], CultureInfo.InvariantCulture))).IsEqualTo(13);
        }

        object?[] last = reference[^1].Singletons;
        Console.WriteLine($"{file}: last frame: T {last[ScoreT]} CT {last[ScoreCt]}");

        foreach (int minChunkFrames in (int[])[1, 8192])
        {
            EntityFrameDigest[] pipelined = Produce(bytes, minChunkFrames, out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans);
            await Assert.That(pipelined.Length).IsEqualTo(reference.Length);
            if (minChunkFrames == 1 && resyncFrame >= 0)
            {
                await Assert.That(spans.Any(s => s.CheckpointFrameIndex == resyncFrame)).IsTrue()
                    .Because("the dense plan must prime a chunk from the full packet the sequential decode resyncs at");
            }

            Dictionary<(int Provider, int Slot), object?> referenceFold = [];
            Dictionary<(int Provider, int Slot), object?> pipelinedFold = [];
            int mismatchFrames = 0;
            string? first = null;
            for (int n = 0; n < reference.Length; n++)
            {
                PipelinedDigestEquivalenceTests.Fold(referenceFold, reference[n]);
                PipelinedDigestEquivalenceTests.Fold(pipelinedFold, pipelined[n]);
                string? diff = PipelinedDigestEquivalenceTests.DiffSingletons(reference[n], pipelined[n])
                               ?? PipelinedDigestEquivalenceTests.DiffSnapshots(referenceFold, pipelinedFold);
                if (diff is not null)
                {
                    mismatchFrames++;
                    first ??= $"frame {n}: {diff}";
                }
            }

            Console.WriteLine($"{file}: minChunkFrames={minChunkFrames} chunks={spans.Count} mismatch frames={mismatchFrames}"
                              + (first is null ? "" : $" first: {first}"));
            await Assert.That(mismatchFrames).IsEqualTo(0).Because($"minChunkFrames={minChunkFrames}: {first}");
        }
    }

    [Test]
    [Arguments("furia-vs-vitality-m1-mirage.dem")]
    [Arguments("furia-vs-vitality-m2-inferno.dem")]
    [Arguments("furia-vs-vitality-m3-nuke.dem")]
    [Arguments("furia-vs-vitality-m4-overpass.dem")]
    [Arguments("vitality-vs-fut-m1-mirage.dem")]
    [Arguments("vitality-vs-fut-m2-dust2.dem")]
    [Arguments("vitality-vs-fut-m3-nuke.dem")]
    public async Task TeamScore_IsTheSame_AtOneCore_AndAtTheDefault(string file)
    {
        string path = DemoTestHelper.RequireDemo(file);
        string sequential = TeamFinal(path, 1);
        string parallel = TeamFinal(path, null);
        Console.WriteLine($"{file}: dop 1 {sequential}  default {parallel}");
        await Assert.That(parallel).IsEqualTo(sequential);
    }

    private static string TeamFinal(string path, int? maxDegreeOfParallelism)
    {
        AnalysisRun run = DemoAnalysis.Run(path, RoundFactsTestSupport.Load(TeamScopeTests.LibraryTeams),
            new AnalysisOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism });
        MetricTable table = RoundFactsTestSupport.Table(run, "team_final");
        return string.Join(";", table.Rows.Select(r =>
            $"{RoundFactsTestSupport.Dim(r, "side")}:{RoundFactsTestSupport.Int(r, "score")}:{r.Values["clan_name"]}"));
    }

    private static EntityFrameDigest[] Produce(byte[] bytes, int minChunkFrames,
        out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans)
    {
        using DemoReader reader = DemoReader.Open(bytes.AsMemory());
        return PipelinedDigests.Produce(reader, PipelinedDigestEquivalenceTests.NewPerPlayer, NewSingletons, false,
            out spans, minChunkFrames: minChunkFrames, releaseFolded: true);
    }
}
