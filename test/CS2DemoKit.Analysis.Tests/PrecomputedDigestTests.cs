#region

using System.Diagnostics;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <see cref="EntityChangeScanner.PrecomputeParallelDigests" /> is the public seam a host uses
///     to run the digest fold apart from the evaluation loop: DemoViewer.NET's micro-bench times
///     it, and its diagnostics read <see cref="ScannerProfilingSnapshot.PrecomputeTicks" />,
///     <see cref="ScannerProfilingSnapshot.PrecomputeAlloc" /> and the <c>analysis.precompute</c>
///     span. The fold runs on the pipelined producer, so an evaluation over what it held must
///     match one the producer served live, and the seam must be bracketed the way the host reads it.
/// </summary>
[NotInParallel]
[Category("Integration")]
public class PrecomputedDigestTests
{
    private static EntityChangeScanner MinimalScanner(IPerPlayerEntityValueProvider provider) =>
        new(new EntityStateLayer(), [], [provider]);

    [Test]
    public async Task Precomputed_ServesTheNextEvaluation_AndMatchesTheLiveProducer()
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();

        BuildResult liveBuild = DemoAnalysis.Build(demo, rules.Rulesets);
        AnalysisRun live = DemoAnalysis.Evaluate(demo, liveBuild);
        string expected = ForwardPathParityTests.RunDigest.Render(live, false);

        BuildResult build = DemoAnalysis.Build(demo, rules.Rulesets);
        List<double> progress = [];
        build.EntityScanner!.PrecomputeParallelDigests(demo.Frames, progress.Add);
        AnalysisRun precomputed = DemoAnalysis.Evaluate(demo, build);

        await Assert.That(precomputed.Provenance.Digest).IsEqualTo(DigestProducerKind.Pipelined);
        await Assert.That(precomputed.Provenance.FramesConsumed).IsEqualTo(demo.Frames.Count);
        await Assert.That(ForwardPathParityTests.RunDigest.Render(precomputed, false)).IsEqualTo(expected);

        await Assert.That(progress.Count).IsGreaterThan(1);
        await Assert.That(progress[^1]).IsEqualTo(1.0);
        for (int i = 1; i < progress.Count; i++)
        {
            await Assert.That(progress[i]).IsGreaterThanOrEqualTo(progress[i - 1]);
        }

        // The held digests serve one evaluation; the next starts the producer again.
        AnalysisRun again = DemoAnalysis.Evaluate(demo, build);
        await Assert.That(again.Provenance.Digest).IsEqualTo(DigestProducerKind.Pipelined);
        await Assert.That(ForwardPathParityTests.RunDigest.Render(again, false)).IsEqualTo(expected);
    }

    [Test]
    public async Task Precompute_IsBracketed_ByTheProfilingSlots_AndTheSpan()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        EntityChangeScanner scanner = MinimalScanner(new PawnHealthProvider());

        List<string> spans = [];
        using ActivityListener listener = new()
        {
            ShouldListenTo = src => src.Name == AnalysisDiagnostics.SourceName,
            Sample = static (ref options) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => spans.Add(activity.OperationName)
        };
        ActivitySource.AddActivityListener(listener);

        bool wasProfiling = Profiling.Enabled;
        try
        {
            Profiling.Enabled = true;
            scanner.PrecomputeParallelDigests(demo.Frames);
        }
        finally
        {
            Profiling.Enabled = wasProfiling;
        }

        ScannerProfilingSnapshot snapshot = scanner.GetProfilingSnapshot();
        await Assert.That(snapshot.Enabled).IsTrue();
        await Assert.That(snapshot.PrecomputeTicks).IsGreaterThan(0L);
        await Assert.That(snapshot.PrecomputeAlloc).IsGreaterThan(0L)
            .Because("the fold allocates on worker threads, which the calling thread's counter never sees");
        await Assert.That(spans).Contains("analysis.precompute");
    }

    [Test]
    public async Task Precompute_Cancelled_HoldsNothing_SoTheNextEvaluationStartsAProducer()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        EntityChangeScanner scanner = MinimalScanner(new PawnHealthProvider());
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        bool cancelled = false;
        try
        {
            scanner.PrecomputeParallelDigests(demo.Frames, cancellationToken: cts.Token);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        await Assert.That(cancelled).IsTrue();

        IDemoFrameSource source = scanner.BeginEvaluation(demo.AsFrameSource(), null, CancellationToken.None);
        try
        {
            await Assert.That(source).IsTypeOf<PipelinedDigestSource>();
        }
        finally
        {
            scanner.EndEvaluation();
        }
    }

    [Test]
    public async Task Precompute_JudgesTheSchema_OnTheFoldWorker()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        EntityChangeScanner scanner = MinimalScanner(new GenericPerPlayerFieldProvider(new ProviderSpec(
            "entity.pawn.bogus", "CCSPlayerPawn", "m_iHealht", typeof(int))));

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => scanner.PrecomputeParallelDigests(demo.Frames));
        await Assert.That(ex.Message).Contains("m_iHealht");
    }
}
