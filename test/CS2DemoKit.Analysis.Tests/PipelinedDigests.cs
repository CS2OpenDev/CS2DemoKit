#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Runs the pipelined producer over a source to the end and hands back every frame's digest
///     in frame order, which is what a gate compares against a sequential fold. The chunk
///     boundaries the reader chose come back alongside, since a boundary is where a worker
///     primes from a checkpoint and re-emits every live cell: the one position a reconstruction
///     bug can hide at.
/// </summary>
internal static class PipelinedDigests
{
    public static EntityFrameDigest[] Produce(
        IDemoFrameSource source,
        Func<IReadOnlyList<IPerPlayerEntityValueProvider>> perPlayerFactory,
        Func<IReadOnlyList<IEntityValueProvider>> singletonFactory,
        bool emitMolotov,
        bool captureSmokes = false,
        int workers = EntityChangeScanner.DefaultPipelineWorkers,
        int minChunkFrames = PipelinedDigestSource.MinChunkFrames,
        bool releaseFolded = false,
        Action<EntityTracker>? schemaCheck = null,
        CancellationToken cancellationToken = default) =>
        Produce(source, perPlayerFactory, singletonFactory, emitMolotov, out _,
            captureSmokes, workers, minChunkFrames, releaseFolded, schemaCheck, cancellationToken);

    public static EntityFrameDigest[] Produce(
        IDemoFrameSource source,
        Func<IReadOnlyList<IPerPlayerEntityValueProvider>> perPlayerFactory,
        Func<IReadOnlyList<IEntityValueProvider>> singletonFactory,
        bool emitMolotov,
        out IReadOnlyList<PipelinedDigestSource.ChunkSpan> spans,
        bool captureSmokes = false,
        int workers = EntityChangeScanner.DefaultPipelineWorkers,
        int minChunkFrames = PipelinedDigestSource.MinChunkFrames,
        bool releaseFolded = false,
        Action<EntityTracker>? schemaCheck = null,
        CancellationToken cancellationToken = default)
    {
        PipelinedDigestSource pipeline = new(source, perPlayerFactory, singletonFactory, emitMolotov, captureSmokes,
            workers, releaseFolded, schemaCheck, cancellationToken, minChunkFrames);
        try
        {
            EntityFrameDigest[] digests = Drain(pipeline);
            spans = pipeline.Spans;
            return digests;
        }
        finally
        {
            pipeline.Close();
        }
    }

    /// <summary>
    ///     The same, through a scanner's own producer choice and provider clones: what an
    ///     evaluation over <paramref name="source" /> runs. The scanner must choose the pipeline.
    /// </summary>
    public static EntityFrameDigest[] Produce(EntityChangeScanner scanner, IDemoFrameSource source,
        int? maxDegreeOfParallelism = null)
    {
        IDemoFrameSource frames = scanner.BeginEvaluation(source, maxDegreeOfParallelism, CancellationToken.None);
        try
        {
            if (frames is not PipelinedDigestSource pipeline)
            {
                throw new InvalidOperationException("the scanner chose the sequential producer, which yields no digest array");
            }

            return Drain(pipeline);
        }
        finally
        {
            scanner.EndEvaluation();
        }
    }

    private static EntityFrameDigest[] Drain(PipelinedDigestSource pipeline)
    {
        List<EntityFrameDigest> digests = [];
        while (pipeline.TryReadNext(out _))
        {
            digests.Add(pipeline.Take(digests.Count));
        }

        return [.. digests];
    }
}

/// <summary>
///     Drives a scanner over a retained demo the way the evaluator drives the sequential
///     producer: one layer, each tick run applied before any frame in it is polled. For tests
///     that read the scanner frame by frame rather than through a graph.
/// </summary>
internal static class SequentialScan
{
    public static IEnumerable<(int FrameIndex, DemoFrame Frame, IReadOnlyList<NetMessage> Messages)> Frames(
        EntityChangeScanner scanner, ParsedDemo demo)
    {
        IDemoFrameSource source = scanner.BeginEvaluation(demo.AsFrameSource(), 1, CancellationToken.None);
        try
        {
            List<DemoFrame> run = new(4);
            int index = 0;
            while (ReadTickRun(source, run))
            {
                scanner.BeginTickRun(run);
                foreach (DemoFrame frame in run)
                {
                    yield return (index, frame, scanner.AdvanceAndPollAt(index, frame.ServerTick));
                    index++;
                }
            }
        }
        finally
        {
            scanner.EndEvaluation();
        }
    }

    private static bool ReadTickRun(IDemoFrameSource source, List<DemoFrame> run)
    {
        run.Clear();
        if (!source.TryReadNext(out DemoFrame? first))
        {
            return false;
        }

        run.Add(first);
        while (source.TryPeekNext(out DemoFrame? next) && next.ServerTick == first.ServerTick
                                                        && source.TryReadNext(out next))
        {
            run.Add(next);
        }

        return true;
    }
}
