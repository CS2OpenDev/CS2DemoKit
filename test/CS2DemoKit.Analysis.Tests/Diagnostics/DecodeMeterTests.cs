#region

using System.Diagnostics.Metrics;
using System.Threading;
using CS2DemoKit.Analysis.Diagnostics;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.Diagnostics;

/// <summary>
///     The decode meter: CS2DemoKit.Parser's <c>parser.entity_decode.started</c> and
///     CS2DemoKit.Analysis's <c>analysis.entity_scan.started</c> (see docs/profiling.md). A rules
///     run silences every tracker it owns and counts itself once on its own counter; a sampler walk
///     or a bare tracker the engine does not own counts on the Parser counter instead.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class DecodeMeterTests
{
    private const int MaxFrames = 2000;

    [Test]
    [Arguments(null)]
    [Arguments(1)]
    public async Task OneRulesRun_IsOneScanAndZeroParserDecodes_AtDefaultDop(int? maxDegreeOfParallelism)
    {
        string path = DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName);
        byte[] bytes = await File.ReadAllBytesAsync(path);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        AnalysisOptions options = new() { MaxDegreeOfParallelism = maxDegreeOfParallelism };

        BuildResult listBuild = DemoAnalysis.Build(demo, rules.Rulesets);
        await Assert.That(listBuild.EntityScanner).IsNotNull()
            .Because("gameplay_phase's builtin chain always references entity.game.freeze_period");

        Counts list = RunUnderListener(() => DemoAnalysis.Evaluate(demo, listBuild, options));
        await Assert.That(list.AnalysisScans).IsEqualTo(1L);
        await Assert.That(list.ParserDecodes).IsEqualTo(0L);

        BuildResult readerBuild = DemoAnalysis.Build(demo, rules.Rulesets);
        Counts stream = RunUnderListener(() =>
        {
            using DemoReader reader = DemoReader.Open(bytes.AsMemory());
            DemoAnalysis.Evaluate(reader, readerBuild, options);
        });
        await Assert.That(stream.AnalysisScans).IsEqualTo(1L);
        await Assert.That(stream.ParserDecodes).IsEqualTo(0L);
    }

    [Test]
    public async Task ASamplerWalk_IsOneParserDecode()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));

        Counts position = RunUnderListener(() => Drain(PositionSampler.Walk(demo, maxFrames: MaxFrames)));
        await Assert.That(position.ParserDecodes).IsEqualTo(1L);
        await Assert.That(position.AnalysisScans).IsEqualTo(0L);

        Counts projectile = RunUnderListener(() => Drain(ProjectileSampler.Walk(demo, maxFrames: MaxFrames)));
        await Assert.That(projectile.ParserDecodes).IsEqualTo(1L);

        // The host's shape: a bare tracker with no curation, replayed directly.
        Counts bare = RunUnderListener(() =>
        {
            EntityTracker tracker = new();
            tracker.Replay(demo.Frames.Take(MaxFrames).ToList());
        });
        await Assert.That(bare.ParserDecodes).IsEqualTo(1L);
    }

    [Test]
    public async Task NoListener_CountsNothing()
    {
        // Proves the Enabled guard, not just its absence: both counters stay unsubscribed across a
        // real walk with no listener attached anywhere in this NotInParallel class.
        await Assert.That(EntityDecodeMetrics.DecodeStarted.Enabled).IsFalse();
        await Assert.That(EvaluatorMetrics.EntityScanStarted.Enabled).IsFalse();

        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo(DemoTestHelper.SampleDemoFileName));
        Drain(PositionSampler.Walk(demo, maxFrames: MaxFrames));

        await Assert.That(EntityDecodeMetrics.DecodeStarted.Enabled).IsFalse();
    }

    private static void Drain<T>(IEnumerable<T> source)
    {
        foreach (T _ in source)
        {
        }
    }

    private sealed class Counts
    {
        public long AnalysisScans;
        public long ParserDecodes;
    }

    // Scopes one run()'s measurements to its own caller: MeterListener callbacks are process-global,
    // and ExecutionContext (so this AsyncLocal) flows onto the pipelined producer's worker threads.
    private static readonly AsyncLocal<Counts?> _active = new();

    private static Counts RunUnderListener(Action run)
    {
        Counts counts = new();
        _active.Value = counts;
        try
        {
            using MeterListener listener = new();
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name is "CS2DemoKit.Parser.Entities" or "CS2DemoKit.Analysis.Evaluator")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            {
                Counts? active = _active.Value;
                if (active is null)
                {
                    return;
                }

                switch (instrument.Name)
                {
                    case "parser.entity_decode.started":
                        Interlocked.Add(ref active.ParserDecodes, measurement);
                        break;
                    case "analysis.entity_scan.started":
                        Interlocked.Add(ref active.AnalysisScans, measurement);
                        break;
                }
            });
            listener.Start();
            run();
        }
        finally
        {
            _active.Value = null;
        }

        return counts;
    }
}
