#region

using CS2DemoKit.TestSupport;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     The reader follows the parser's malformed-demo policy: only a non-demo throws, at open;
///     damage inside a real demo ends the stream with a warning and a reason, and what was read
///     stays usable. Plus the lifecycle rules: cancellation, disposal, and the before-first-read
///     operations.
/// </summary>
[Category("Unit")]
public class DemoReaderPolicyTests
{
    /// <summary>A 16-byte header good enough to pass the magic check, then <paramref name="body" />.</summary>
    private static byte[] DemoWith(params byte[] body)
    {
        byte[] file = new byte[16 + body.Length];
        "PBDEMS2\0"u8.CopyTo(file);
        body.CopyTo(file, 16);
        return file;
    }

    private static (List<DemoFrame> Frames, ReadEndReason? End, IReadOnlyList<ParseWarning> Warnings, ParseHealth Health) Drain(byte[] file, int readAhead = 0)
    {
        using DemoReader reader = DemoReader.Open(file.AsMemory(), new ParseOptions { ReadAheadFrames = readAhead });
        List<DemoFrame> frames = [.. reader.ReadFrames()];
        return (frames, reader.EndReason, reader.Enrichment.Warnings, reader.Enrichment.Health);
    }

    /// <summary>A window ends the stream exactly as the sequential read does, once it is drained.</summary>
    [Test]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x07 })]
    public async Task WindowedReadAhead_EndsTheStreamTheSameWay(byte[] body)
    {
        (List<DemoFrame> sequential, ReadEndReason? seqEnd, IReadOnlyList<ParseWarning> seqWarnings, ParseHealth seqHealth) =
            Drain(DemoWith(body));
        foreach (int readAhead in new[] { 1, 2, 64 })
        {
            (List<DemoFrame> windowed, ReadEndReason? end, IReadOnlyList<ParseWarning> warnings, ParseHealth health) =
                Drain(DemoWith(body), readAhead);
            await Assert.That(windowed.Count).IsEqualTo(sequential.Count).Because($"readAhead={readAhead}");
            for (int i = 0; i < sequential.Count; i++)
            {
                await Assert.That(windowed[i].FrameNumber).IsEqualTo(sequential[i].FrameNumber);
                await Assert.That(windowed[i].CommandKind).IsEqualTo(sequential[i].CommandKind);
                await Assert.That(windowed[i].ServerTick).IsEqualTo(sequential[i].ServerTick);
            }

            await Assert.That(end).IsEqualTo(seqEnd).Because($"readAhead={readAhead}");
            await Assert.That(string.Join("|", warnings.Select(w => $"{w.Code}:{w.Count}")))
                .IsEqualTo(string.Join("|", seqWarnings.Select(w => $"{w.Code}:{w.Count}")));
            await Assert.That(health).IsEqualTo(seqHealth);
        }
    }

    /// <summary>
    ///     The whole-file parse ends on damage exactly as the read does, with the same warnings and
    ///     health, and the reader is final once it returns.
    /// </summary>
    [Test]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0x07 })]
    [Arguments(new byte[] { 0x03, 0x00, 0x00, 0x01, 0x00, 0x80, 0x80, 0x80, 0x80, 0x08 })]
    public async Task Materialize_EndsOnDamageTheSameWay_AndLeavesTheReaderFinal(byte[] body)
    {
        (List<DemoFrame> sequential, ReadEndReason? seqEnd, IReadOnlyList<ParseWarning> seqWarnings, ParseHealth seqHealth) =
            Drain(DemoWith(body));

        using DemoReader reader = DemoReader.Open(DemoWith(body).AsMemory(), new ParseOptions { ReadAheadFrames = 2 });
        ParsedDemo demo = reader.Materialize();

        await Assert.That(demo.Frames.Count).IsEqualTo(sequential.Count);
        for (int i = 0; i < sequential.Count; i++)
        {
            await Assert.That(demo.Frames[i].FrameNumber).IsEqualTo(sequential[i].FrameNumber);
            await Assert.That(demo.Frames[i].CommandKind).IsEqualTo(sequential[i].CommandKind);
            await Assert.That(demo.Frames[i].ServerTick).IsEqualTo(sequential[i].ServerTick);
        }

        await Assert.That(reader.EndReason).IsEqualTo(seqEnd);
        await Assert.That(string.Join("|", demo.Warnings.Select(w => $"{w.Code}:{w.Count}")))
            .IsEqualTo(string.Join("|", seqWarnings.Select(w => $"{w.Code}:{w.Count}")));
        await Assert.That(demo.Health).IsEqualTo(seqHealth);
        await Assert.That(reader.Enrichment.Health).IsEqualTo(seqHealth);

        await Assert.That(demo.Provenance.Source).IsEqualTo(DecodeSource.DemoParserParse);
        await Assert.That(demo.Provenance.Mode).IsEqualTo(DecodeMode.ParallelWholeFile);
        await Assert.That(demo.Provenance.ReadAheadFrames).IsEqualTo(0);
        await Assert.That(demo.Provenance.FramesRead).IsEqualTo((long)sequential.Count);
        await Assert.That(reader.Provenance).IsEqualTo(demo.Provenance);
        await Assert.That(reader.Started).IsTrue();
        Assert.Throws<InvalidOperationException>(() => reader.Materialize());
        await Assert.That(reader.TryReadNext(out _)).IsFalse();
    }

    [Test]
    public async Task Materialize_RunsInsteadOfARead_AndHonoursTheToken()
    {
        byte[] file = DemoWith(0x03, 0x00, 0x00, 0x03, 0x01, 0x00);
        using (DemoReader read = DemoReader.Open(file.AsMemory()))
        {
            await Assert.That(read.TryReadNext(out _)).IsTrue();
            Assert.Throws<InvalidOperationException>(() => read.Materialize());
        }

        using CancellationTokenSource cts = new();
        await cts.CancelAsync();
        using DemoReader cancelled = DemoReader.Open(file.AsMemory(), new ParseOptions { CancellationToken = cts.Token });
        Assert.Throws<OperationCanceledException>(() => cancelled.Materialize());
        await Assert.That(cancelled.EndReason).IsNull();
    }

    [Test]
    public async Task Open_NotADemo_Throws()
    {
        byte[] notADemo = new byte[64];
        "NOTADEMO"u8.CopyTo(notADemo);
        Assert.Throws<InvalidDataException>(() => DemoReader.Open(notADemo.AsMemory()));
        Assert.Throws<InvalidDataException>(() => DemoReader.Open(new byte[8].AsMemory()));
    }

    [Test]
    public async Task Stop_EndsTheStream_AndIsNotYielded()
    {
        // One DEM_SyncTick (cmd 3, tick 0, size 0), then DEM_Stop, then a frame nobody should see.
        (List<DemoFrame> frames, ReadEndReason? end, IReadOnlyList<ParseWarning> warnings, ParseHealth health) =
            Drain(DemoWith(0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00));

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(frames[0].CommandKind).IsEqualTo(EDemoCommands.DemSyncTick);
        await Assert.That(end).IsEqualTo(ReadEndReason.Stop);
        await Assert.That(warnings.Count).IsEqualTo(0);
        await Assert.That(health).IsEqualTo(ParseHealth.Clean);
    }

    [Test]
    public async Task EndOfData_OnAFrameBoundary_IsClean()
    {
        (List<DemoFrame> frames, ReadEndReason? end, IReadOnlyList<ParseWarning> warnings, _) =
            Drain(DemoWith(0x03, 0x00, 0x00));

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(end).IsEqualTo(ReadEndReason.EndOfData);
        await Assert.That(warnings.Count).IsEqualTo(0);
    }

    [Test]
    public async Task TruncatedHeader_WarnsAndEnds()
    {
        (List<DemoFrame> frames, ReadEndReason? end, IReadOnlyList<ParseWarning> warnings, ParseHealth health) =
            Drain(DemoWith(0x03, 0x00, 0x00, 0x03, 0x00));

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(end).IsEqualTo(ReadEndReason.Truncated);
        await Assert.That(warnings.Any(w => w.Code == ParseWarningCodes.DemoTruncated)).IsTrue();
        await Assert.That(health).IsEqualTo(ParseHealth.Damaged);
    }

    [Test]
    public async Task TruncatedPayload_WarnsAndEnds()
    {
        (List<DemoFrame> frames, ReadEndReason? end, IReadOnlyList<ParseWarning> warnings, _) =
            Drain(DemoWith(0x03, 0x00, 0x00, 0x03, 0x00, 0x05, 0x01));

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(end).IsEqualTo(ReadEndReason.Truncated);
        await Assert.That(warnings.Any(w => w.Code == ParseWarningCodes.DemoTruncated)).IsTrue();
    }

    [Test]
    public async Task ImpossibleSize_WarnsAndEnds()
    {
        // cmd=1, tick=0, size = 0x80000000 as a 5-byte varint: negative once cast to int.
        (List<DemoFrame> frames, ReadEndReason? end, IReadOnlyList<ParseWarning> warnings, ParseHealth health) =
            Drain(DemoWith(0x03, 0x00, 0x00, 0x01, 0x00, 0x80, 0x80, 0x80, 0x80, 0x08));

        await Assert.That(frames.Count).IsEqualTo(1);
        await Assert.That(end).IsEqualTo(ReadEndReason.Corrupt);
        await Assert.That(warnings.Any(w => w.Code == ParseWarningCodes.FrameStreamCorrupt)).IsTrue();
        await Assert.That(health).IsEqualTo(ParseHealth.Damaged);
    }

    [Test]
    public async Task Cancellation_ThrowsFromTheRead_AndLosesNoFrame()
    {
        using CancellationTokenSource cts = new();
        using DemoReader reader = DemoReader.Open(DemoWith(0x03, 0x00, 0x00, 0x03, 0x01, 0x00).AsMemory(),
            new ParseOptions { CancellationToken = cts.Token });

        await Assert.That(reader.TryReadNext(out DemoFrame? first)).IsTrue();
        await Assert.That(first!.ServerTick).IsEqualTo(0);
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => reader.TryReadNext(out _));
        await Assert.That(reader.EndReason).IsNull();
    }

    [Test]
    public async Task Dispose_EndsTheReader_ButNotItsFrames()
    {
        DemoReader reader = DemoReader.Open(DemoWith(0x03, 0x00, 0x00, 0x03, 0x01, 0x00).AsMemory());
        await Assert.That(reader.TryReadNext(out DemoFrame? first)).IsTrue();
        reader.Dispose();
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.TryReadNext(out _));
        Assert.Throws<ObjectDisposedException>(() => reader.TryPeekNext(out _));
        Assert.Throws<ObjectDisposedException>(() => reader.Configure(DecodePlan.Everything));
        await Assert.That(first!.Command).IsEqualTo("DEM_SyncTick");
    }

    [Test]
    public async Task BeforeTheFirstRead_ConfigureAndProbeAreAllowed()
    {
        using DemoReader reader = DemoReader.Open(DemoWith(0x03, 0x00, 0x00, 0x00, 0x00, 0x00).AsMemory());
        await Assert.That(reader.Plan).IsSameReferenceAs(DecodePlan.Everything);
        reader.Configure(DecodePlan.StructureOnly);
        await Assert.That(reader.Plan).IsSameReferenceAs(DecodePlan.StructureOnly);
        IReadOnlySet<string> names = reader.ProbeGameEventNames();
        await Assert.That(names.Count).IsEqualTo(0);
        await Assert.That(reader.Position).IsEqualTo(16L);
        await Assert.That(reader.ReadFrames().Count()).IsEqualTo(1);
        await Assert.That(reader.Provenance.FramesRead).IsEqualTo(1L);
        await Assert.That(reader.EndReason).IsEqualTo(ReadEndReason.Stop);
    }

    [Test]
    [Category("Integration")]
    public async Task OpenFile_OwnsTheMapping_AndReleasesItOnDispose()
    {
        string path = DemoTestHelper.RequireDemo();
        long finalizersBefore = MemoryMappedDemoSource.FinalizerReleaseCount;
        DemoFrame? kept;
        using (DemoReader reader = DemoReader.OpenFile(path, new ParseOptions { Plan = DecodePlan.GameEventsOnly }))
        {
            await Assert.That(reader.Length).IsEqualTo(new FileInfo(path).Length);
            await Assert.That(reader.TryReadNext(out kept)).IsTrue();
            int count = 1;
            while (reader.TryReadNext(out _))
            {
                count++;
            }

            await Assert.That(count).IsGreaterThan(1);
            await Assert.That(reader.EndReason).IsEqualTo(ReadEndReason.Stop);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Assert.That(MemoryMappedDemoSource.FinalizerReleaseCount).IsEqualTo(finalizersBefore)
            .Because("Dispose released the mapping, so the finalizer had nothing to do");
        await Assert.That(kept!.DecodedMessages.Count).IsGreaterThanOrEqualTo(0);
        await Assert.That(kept.Command).IsNotEmpty();
    }
}
