#region

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using CS2DemoKit.TestSupport;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Gates <see cref="ParseOptions" /> — the 0.8 per-parse knobs added to
///     <see cref="DemoParser.Parse(ReadOnlyMemory{byte},ParseOptions,DemoProfile)" />: cooperative
///     cancellation, a pass-2 parallelism cap, throttled progress, a per-parse unknown-message
///     callback, and opt-in net-message drop-site counting.
///     <para>
///         <b>Two fixture tiers.</b> The scheduling knobs (cancellation, DOP, progress) need a real
///         demo with enough frames for pass 2 to be genuinely parallel, so they run against the
///         reference demo. The drop-site and callback contracts need frames the reference demo does
///         not contain (unknown type IDs, undecodable known types, a truncated bitstream), so they
///         run against demos synthesized here — a real <c>CDemoPacket</c> whose <c>data</c> is a
///         hand-written bitstream, wrapped in a hand-written frame header.
///     </para>
///     <para>
///         <b>Trailing-truncation artifact.</b> A synthesized bitstream whose bit length is not a
///         multiple of 8 leaves zero padding in the final byte, and the parser's
///         <c>while (RemainingBits > 0)</c> loop reads it as one more (zero) message header, which
///         trips the size guard and so counts one extra <c>&lt;bitstream-truncated&gt;</c> event.
///         It can never produce a spurious unknown-message callback (the size guard runs BEFORE the
///         parse), so assertions below key on specific type names rather than on totals.
///     </para>
/// </summary>
[NotInParallel]
public class ParseOptionsTests
{
    /// <summary>A type ID no switch arm in <c>ParseNetMessage</c> claims — resolves to "unknown(N)".</summary>
    private const int UnknownTypeId = 400;

    /// <summary>A KNOWN type ID (net_Tick) whose payload below is deliberately undecodable.</summary>
    private const int KnownTypeId = (int)NET_Messages.NetTick;

    /// <summary>One byte that is an invalid protobuf tag (wire type 6), so ParseFrom always throws.</summary>
    private static readonly byte[] _undecodableProtoBytes = [0x0E];

    // ── Default-path identity ────────────────────────────────────────────────

    /// <summary>
    ///     An empty <c>ParseOptions</c> must be indistinguishable from the options-less overload —
    ///     both funnel through the same private core, and every new branch short-circuits on a null
    ///     or default. Parsed sequentially so only one <see cref="ParsedDemo" /> is live at a time.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task EmptyOptions_ParsesIdenticallyToTheOptionsLessOverload()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);

        (int Frames, int Events, int Players, int Ticks, int Warnings) Shape(ParsedDemo d) =>
            (d.Frames.Count, d.AllGameEvents.Count, d.Players.Count, d.TickCount, d.Warnings.Count);

        // S14 (the ±1 Warnings.Count flake, root-caused 2026-08-14): Warn's backing store is
        // [ThreadStatic] and StringTableBoundsTests' hostile-table paths call Warn through
        // DecodeEntries without ever constructing a ParsedDemo, stranding warnings on a pool
        // thread. The FIRST parse below then drains that residue into ITS result while the
        // second parses clean — the exact ±1 this test flaked with. Drain here is airtight:
        // no await sits between it and the two synchronous Parse calls, so all three run on
        // this one thread. Same idiom as the other count-sensitive tests in this file.
        ParseDiagnostics.Drain();

        (int, int, int, int, int) withoutOptions = Shape(DemoParser.Parse(bytes.AsMemory()));
        (int, int, int, int, int) withEmptyOptions = Shape(DemoParser.Parse(bytes.AsMemory(), new ParseOptions()));

        Console.WriteLine($"options-less: {withoutOptions}");
        Console.WriteLine($"empty options: {withEmptyOptions}");
        await Assert.That(withEmptyOptions).IsEqualTo(withoutOptions);
    }

    /// <summary>
    ///     A null <see cref="ParseOptions" /> is rejected at the boundary rather than silently
    ///     behaving like the options-less overload — the cast is load-bearing, see
    ///     <see cref="BareNullSecondArgument_BindsToTheProfileOverload_NotTheOptionsOverload" />.
    /// </summary>
    [Test]
    public async Task NullOptions_Throws()
    {
        byte[] demo = BuildDemo(PacketFrame(Message(UnknownTypeId, [1, 2, 3])));

        Exception? caught = null;
        try
        {
            DemoParser.Parse(demo.AsMemory(), (ParseOptions)null!);
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsTypeOf<ArgumentNullException>();
    }

    /// <summary>
    ///     Overload-resolution pin. Both <see cref="ParseOptions" /> and <see cref="DemoProfile" />
    ///     are reference types, so <c>Parse(data, null)</c> could in principle be ambiguous. It is
    ///     not: the two-parameter overload wins (fewer optional parameters to fill), so a bare null
    ///     silently means "no profile override", NOT "null options". Pinned because the difference
    ///     is invisible at the call site and would otherwise be discovered as a behavior change the
    ///     first time someone reorders or removes an overload.
    /// </summary>
    [Test]
    public async Task BareNullSecondArgument_BindsToTheProfileOverload_NotTheOptionsOverload()
    {
        byte[] demo = BuildDemo(PacketFrame(Message(UnknownTypeId, [1, 2, 3])));

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), null);

        await Assert.That(parsed.Frames).HasCount().EqualTo(1)
            .Because("binding to the ParseOptions overload would have thrown ArgumentNullException");
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Checkpoint 1 (before pass 1): an already-canceled token aborts before any frame is
    ///     scanned. Uses the reference demo so "returned quickly" means something.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task PreCanceledToken_ThrowsBeforeParsing()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        Exception? caught = null;
        ParsedDemo? result = null;
        try
        {
            result = DemoParser.Parse(bytes.AsMemory(), new ParseOptions
            {
                CancellationToken = cts.Token
            });
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsAssignableTo<OperationCanceledException>();
        await Assert.That(result).IsNull().Because("no partial ParsedDemo is ever returned");
    }

    /// <summary>
    ///     Checkpoint 2 (inside pass 2): cancelling from the progress callback — i.e. mid-fan-out —
    ///     surfaces as a bare <see cref="OperationCanceledException" />, NOT an
    ///     <see cref="AggregateException" /> wrapping one. That is a real contract, not an
    ///     accident: the token thrown inside the body is the same one wired into
    ///     <c>ParallelOptions.CancellationToken</c>, so the TPL rethrows it directly.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task CancelDuringPass2_ThrowsOperationCanceled_WithNoPartialResult()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);
        using CancellationTokenSource cts = new();

        int reports = 0;
        CallbackProgress progress = new(_ =>
        {
            Interlocked.Increment(ref reports);
            cts.Cancel();
        });

        Exception? caught = null;
        ParsedDemo? result = null;
        try
        {
            result = DemoParser.Parse(bytes.AsMemory(), new ParseOptions
            {
                CancellationToken = cts.Token,
                Progress = progress
            });
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        Console.WriteLine($"progress reports before cancellation took effect: {reports}");
        await Assert.That(reports).IsGreaterThan(0).Because("cancellation must fire from INSIDE pass 2");
        await Assert.That(caught).IsAssignableTo<OperationCanceledException>();
        await Assert.That(caught).IsNotAssignableTo<AggregateException>();
        await Assert.That(result).IsNull();
    }

    // ── MaxDegreeOfParallelism ───────────────────────────────────────────────

    /// <summary>
    ///     The cap constrains the real pass-2 fan-out. <c>DemoParser</c> exposes no per-worker
    ///     factory seam (unlike <c>ParallelDigestProducer</c>), so the probe rides the one hook
    ///     <see cref="ParseOptions" /> gives into the loop body: <see cref="ParseOptions.Progress" />
    ///     is reported from the pass-2 worker threads, so holding briefly inside it makes concurrent
    ///     workers observably overlap.
    ///     <para>
    ///         The uncapped run is the control: without it, "peak ≤ cap" would pass vacuously on a
    ///         probe that simply never observes overlap.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task MaxDegreeOfParallelism_CapsPass2Concurrency()
    {
        if (Environment.ProcessorCount < 2)
        {
            throw new TUnit.Core.Exceptions.SkipTestException(
                $"needs >= 2 cores (got {Environment.ProcessorCount})");
        }

        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);

        int Peak(int? dop)
        {
            ConcurrencyProbe probe = new(TimeSpan.FromMilliseconds(25), 30);
            ParsedDemo demo = DemoParser.Parse(bytes.AsMemory(), new ParseOptions
            {
                MaxDegreeOfParallelism = dop,
                Progress = probe
            });

            Console.WriteLine($"dop={(dop is null ? "unbounded" : dop.Value.ToString(CultureInfo.InvariantCulture))}  " +
                              $"frames={demo.Frames.Count:N0}  " +
                              $"reports={probe.Reports}  peak={probe.PeakConcurrency}");
            return probe.PeakConcurrency;
        }

        int uncapped = Peak(null);
        await Assert.That(uncapped).IsGreaterThan(1)
            .Because("the probe must be able to SEE concurrency, or the capped assertions are vacuous");

        await Assert.That(Peak(1)).IsEqualTo(1);
        await Assert.That(Peak(2)).IsLessThanOrEqualTo(2);
    }

    /// <summary>Nonsense values degrade to unbounded rather than throwing out of a parse.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(-1)]
    [Arguments(-8)]
    public async Task NonPositiveDop_DegradesToUnbounded_RatherThanThrowing(int dop)
    {
        byte[] demo = BuildDemo(PacketFrame(Message(UnknownTypeId, [1, 2, 3])));

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), new ParseOptions
        {
            MaxDegreeOfParallelism = dop
        });

        await Assert.That(parsed.Frames).HasCount().EqualTo(1);
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Progress is throttled to ~200 reports total regardless of frame count, every value is a
    ///     legal fraction, and the last value is exactly 1.0.
    ///     <para>
    ///         Deliberately NOT asserted: strict arrival-order monotonicity. Reports are raised
    ///         independently from pass-2 workers, so two either side of a throttle boundary can
    ///         arrive out of order — documented on <see cref="ParseOptions.Progress" />. What IS
    ///         guaranteed, and asserted, is that the SET of values is bounded, in range, and
    ///         terminates at 1.0.
    ///     </para>
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task Progress_IsThrottled_InRange_AndEndsAtOne()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] bytes = await File.ReadAllBytesAsync(path);

        ConcurrentBag<double> values = new();
        ParsedDemo demo = DemoParser.Parse(bytes.AsMemory(), new ParseOptions
        {
            Progress = new CallbackProgress(v => values.Add(v))
        });

        double[] observed = values.ToArray();
        Console.WriteLine($"frames={demo.Frames.Count:N0}  reports={observed.Length}  max={observed.Max()}");

        await Assert.That(demo.Frames.Count).IsGreaterThan(10_000)
            .Because("the throttle is only meaningful when frames >> report budget");
        await Assert.That(observed.Length).IsBetween(150, 210).WithInclusiveBounds()
            .Because("~200 reports total, independent of demo size (per-frame reporting would be ~frames)");
        await Assert.That(observed.Min()).IsGreaterThan(0d);
        await Assert.That(observed.Max()).IsEqualTo(1d);
    }

    // ── Per-parse unknown-message callback ───────────────────────────────────

    /// <summary>
    ///     The per-parse callback fires IN ADDITION to the static event, never instead of it, with
    ///     the same <see cref="UnknownMessageInfo" /> payload.
    /// </summary>
    [Test]
    public async Task OnUnknownMessage_FiresAlongsideTheStaticEvent()
    {
        byte[] demo = BuildDemo(PacketFrame(
            Message(UnknownTypeId, [1, 2, 3]),
            Message(UnknownTypeId, [4, 5]),
            Message(UnknownTypeId, [6]),
            Message(UnknownTypeId, [7, 8, 9, 10])));

        ConcurrentBag<UnknownMessageInfo> viaCallback = new();
        ConcurrentBag<UnknownMessageInfo> viaEvent = new();
        Action<UnknownMessageInfo> handler = info => viaEvent.Add(info);

        DemoParser.OnUnknownMessageType += handler;
        try
        {
            DemoParser.Parse(demo.AsMemory(), new ParseOptions
            {
                OnUnknownMessage = info => viaCallback.Add(info)
            });
        }
        finally
        {
            DemoParser.OnUnknownMessageType -= handler;
        }

        await Assert.That(viaCallback).HasCount().EqualTo(4);
        await Assert.That(viaCallback.Select(i => i.TypeId).Distinct()).HasCount().EqualTo(1);
        await Assert.That(viaCallback.First().TypeId).IsEqualTo(UnknownTypeId);
        await Assert.That(viaCallback.First().TypeName).IsEqualTo($"unknown({UnknownTypeId})");
        await Assert.That(viaEvent.Count(i => i.TypeId == UnknownTypeId)).IsEqualTo(4)
            .Because("the static event still fires — the callback is additive, not a replacement");
    }

    /// <summary>
    ///     The defect the per-parse callback exists to fix: with the process-global event, a
    ///     subscriber sees EVERY concurrent parse's occurrences interleaved. Two parses run
    ///     concurrently, each with its own callback and its own unknown type ID; each callback must
    ///     observe only its own parse. The static event is asserted to see BOTH — its cross-talk is
    ///     intentional back-compat, and pinning it here keeps that a choice rather than a surprise.
    /// </summary>
    [Test]
    public async Task ConcurrentParses_PerParseCallbacksDoNotCrossTalk()
    {
        const int TypeA = 401;
        const int TypeB = 402;
        byte[] demoA = BuildDemo(PacketFrame(
            Message(TypeA, [1]), Message(TypeA, [2]), Message(TypeA, [3]), Message(TypeA, [4])));
        byte[] demoB = BuildDemo(PacketFrame(
            Message(TypeB, [1]), Message(TypeB, [2]), Message(TypeB, [3]), Message(TypeB, [4])));

        ConcurrentBag<int> seenByA = new();
        ConcurrentBag<int> seenByB = new();
        ConcurrentBag<int> seenByStaticEvent = new();
        Action<UnknownMessageInfo> handler = info => seenByStaticEvent.Add(info.TypeId);

        DemoParser.OnUnknownMessageType += handler;
        try
        {
            // Same buffers parsed many times over: one pass of two parses can finish before the
            // other starts, which would let cross-talk hide. Repeating drives real overlap.
            for (int round = 0; round < 8; round++)
            {
                Task a = Task.Run(() => DemoParser.Parse(demoA.AsMemory(), new ParseOptions
                {
                    OnUnknownMessage = info => seenByA.Add(info.TypeId)
                }));
                Task b = Task.Run(() => DemoParser.Parse(demoB.AsMemory(), new ParseOptions
                {
                    OnUnknownMessage = info => seenByB.Add(info.TypeId)
                }));
                await Task.WhenAll(a, b);
            }
        }
        finally
        {
            DemoParser.OnUnknownMessageType -= handler;
        }

        Console.WriteLine($"A saw {seenByA.Count}, B saw {seenByB.Count}, " +
                          $"static event saw {seenByStaticEvent.Count}");

        await Assert.That(seenByA.Distinct()).IsEquivalentTo(new[] { TypeA })
            .Because("a per-parse callback must never observe another parse's occurrences");
        await Assert.That(seenByB.Distinct()).IsEquivalentTo(new[] { TypeB });
        await Assert.That(seenByA).HasCount().EqualTo(32);
        await Assert.That(seenByB).HasCount().EqualTo(32);
        await Assert.That(seenByStaticEvent.Distinct().Order()).IsEquivalentTo(new[] { TypeA, TypeB })
            .Because("the static event is process-global by design — that is what the callback replaces");
    }

    // ── Drop-site counting ───────────────────────────────────────────────────

    [Test]
    public async Task CountDropSites_OffByDefault_EmitsNoDropWarnings()
    {
        ParseDiagnostics.Drain();
        byte[] demo = BuildDemo(PacketFrame(
            Message(UnknownTypeId, [1]), Message(UnknownTypeId, [2]),
            Message(UnknownTypeId, [3]), Message(UnknownTypeId, [4])));

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), new ParseOptions());

        await Assert.That(DropWarnings(parsed)).IsEmpty();
    }

    /// <summary>Drop site 1 — an unknown type ID, counted under its resolved "unknown(N)" name.</summary>
    [Test]
    public async Task CountDropSites_UnknownTypeId_IsCountedUnderItsResolvedName()
    {
        ParseDiagnostics.Drain();
        byte[] demo = BuildDemo(PacketFrame(
            Message(UnknownTypeId, [1]), Message(UnknownTypeId, [2]),
            Message(UnknownTypeId, [3]), Message(UnknownTypeId, [4])));

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), new ParseOptions
        {
            CountDropSites = true
        });

        ParseWarning entry = RequireDropWarning(parsed, $"unknown({UnknownTypeId})");
        await Assert.That(entry.Count).IsEqualTo(4);
        await Assert.That(entry.Code).IsEqualTo(ParseWarningCodes.NetMessageDropped);
    }

    /// <summary>Drop site 2 — a KNOWN type whose protobuf decode failed, counted under its proto name.</summary>
    [Test]
    public async Task CountDropSites_KnownTypeThatFailsToDecode_IsCountedUnderItsProtoName()
    {
        ParseDiagnostics.Drain();
        byte[] demo = BuildDemo(PacketFrame(
            Message(KnownTypeId, _undecodableProtoBytes), Message(KnownTypeId, _undecodableProtoBytes),
            Message(KnownTypeId, _undecodableProtoBytes), Message(KnownTypeId, _undecodableProtoBytes)));

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), new ParseOptions
        {
            CountDropSites = true
        });

        ParseWarning entry = RequireDropWarning(parsed, "net_Tick");
        await Assert.That(entry.Count).IsEqualTo(4);
        await Assert.That(parsed.Frames[0].InnerMessages).IsEmpty()
            .Because("the drop is real — the message never reaches the frame");
    }

    /// <summary>
    ///     Drop site 3 — a size varint larger than the bitstream holds abandons every remaining
    ///     message in the frame, and is counted once per truncation EVENT (the number of messages
    ///     it abandons is unknowable from where the break happens).
    /// </summary>
    [Test]
    public async Task CountDropSites_TruncatedBitstream_IsCountedOncePerEvent()
    {
        ParseDiagnostics.Drain();
        // One header declaring 200 payload bytes over a bitstream holding 3, in each of two frames.
        byte[] frame = PacketFrameRaw(new Bits().UBitVar(UnknownTypeId).VarInt(200).Raw(1, 8).Raw(2, 8).Raw(3, 8));
        byte[] demo = BuildDemo(frame, frame);

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), new ParseOptions
        {
            CountDropSites = true
        });

        ParseWarning entry = RequireDropWarning(parsed, "<bitstream-truncated>");
        await Assert.That(entry.Count).IsEqualTo(2).Because("one event per frame, not one per abandoned message");
    }

    /// <summary>
    ///     A corrupted upload can synthesize an unbounded number of distinct garbage type IDs. The
    ///     emission is capped at the top 8 by count plus one remainder summary, so the drop tally can
    ///     never consume more than 9 of the shared 256-entry warning budget however damaged the input.
    /// </summary>
    [Test]
    public async Task CountDropSites_ManyDistinctTypes_CapAtTopEightPlusRemainder()
    {
        ParseDiagnostics.Drain();
        // 12 distinct unknown type IDs, occurrence counts 12, 11, … 1 — so the ranking is total.
        List<Bits> messages = new();
        for (int t = 0; t < 12; t++)
        {
            for (int n = 0; n < 12 - t; n++)
            {
                messages.Add(Message(500 + t, [(byte)t]));
            }
        }

        ParsedDemo parsed = DemoParser.Parse(BuildDemo(PacketFrame(messages.ToArray())).AsMemory(), new ParseOptions
        {
            CountDropSites = true
        });

        List<ParseWarning> drops = DropWarnings(parsed);
        foreach (ParseWarning w in drops)
        {
            Console.WriteLine($"  {w.Code}  {w.Message}  count={w.Count}");
        }

        await Assert.That(drops).HasCount().EqualTo(9)
            .Because("8 distinct types + 1 remainder summary, no matter how many types were dropped");
        await Assert.That(drops[0].Message).StartsWith("unknown(500)")
            .Because("entries are ordered by count, descending");
        await Assert.That(drops[0].Count).IsEqualTo(12);
        await Assert.That(drops[^1].Message).Contains("more distinct type(s) dropped");
        // Remainder: ranks 9-12 (counts 4, 3, 2, 1) plus the padding truncation event.
        await Assert.That(drops[^1].Count ?? 0).IsGreaterThanOrEqualTo(10);
    }

    /// <summary>
    ///     The ordering half of the budget fix: drop counts are emitted at the END of pass 3, after
    ///     every warning pass 3's own frame-walk produces, so the structural-damage warnings claim
    ///     the shared budget first. Directly observable — <c>ParseDiagnostics</c> keeps warnings in
    ///     arrival order, so "emitted last" means "later in <see cref="ParsedDemo.Warnings" />".
    /// </summary>
    [Test]
    public async Task CountDropSites_AreEmittedAfterPass3sOwnWarnings()
    {
        ParseDiagnostics.Drain();
        // A svc_CreateStringTable that PARSES as protobuf but whose declared entry count cannot fit
        // in its string_data — pass 3 rejects the table and warns. Alongside it, dropped messages.
        byte[] createStringTable = new CSVCMsg_CreateStringTable
        {
            Name = "userinfo",
            NumEntries = 100_000,
            StringData = ByteString.CopyFrom([1, 2, 3])
        }.ToByteArray();

        byte[] demo = BuildDemo(PacketFrame(
            Message((int)SVC_Messages.SvcCreateStringTable, createStringTable),
            Message(UnknownTypeId, [1]),
            Message(UnknownTypeId, [2]),
            Message(UnknownTypeId, [3])));

        ParsedDemo parsed = DemoParser.Parse(demo.AsMemory(), new ParseOptions
        {
            CountDropSites = true
        });

        foreach (ParseWarning w in parsed.Warnings)
        {
            Console.WriteLine($"  {w.Code}  {w.Message}  count={w.Count}");
        }

        int stringTableAt = IndexOfCode(parsed.Warnings, ParseWarningCodes.StringTableCreateFailed);
        int firstDropAt = IndexOfCode(parsed.Warnings, ParseWarningCodes.NetMessageDropped);

        await Assert.That(stringTableAt).IsGreaterThanOrEqualTo(0)
            .Because("pass 3 must still get its own warning through");
        await Assert.That(firstDropAt).IsGreaterThan(stringTableAt)
            .Because("drop counts are emitted last, so pass 3's warnings claim the budget first");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<ParseWarning> DropWarnings(ParsedDemo demo) =>
        demo.Warnings.Where(w => w.Code == ParseWarningCodes.NetMessageDropped).ToList();

    private static ParseWarning RequireDropWarning(ParsedDemo demo, string typeName)
    {
        foreach (ParseWarning w in demo.Warnings)
        {
            Console.WriteLine($"  {w.Code}  {w.Message}  count={w.Count}");
        }

        return DropWarnings(demo).Single(w => w.Message.StartsWith(typeName, StringComparison.Ordinal));
    }

    private static int IndexOfCode(IReadOnlyList<ParseWarning> warnings, string code)
    {
        for (int i = 0; i < warnings.Count; i++)
        {
            if (warnings[i].Code == code)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>One inner message: UBitVar type id, uvarint size, then <paramref name="payload" />.</summary>
    private static Bits Message(int typeId, byte[] payload)
    {
        Bits bits = new Bits().UBitVar((uint)typeId).VarInt((uint)payload.Length);
        foreach (byte b in payload)
        {
            bits.Raw(b, 8);
        }

        return bits;
    }

    /// <summary>A DEM_Packet frame payload wrapping <paramref name="messages" /> in a real CDemoPacket.</summary>
    private static byte[] PacketFrame(params Bits[] messages)
    {
        Bits all = new();
        foreach (Bits m in messages)
        {
            all.Append(m);
        }

        return PacketFrameRaw(all);
    }

    private static byte[] PacketFrameRaw(Bits bitstream) =>
        new CDemoPacket
        {
            Data = ByteString.CopyFrom(bitstream.ToArray())
        }.ToByteArray();

    /// <summary>
    ///     A minimal .dem container: the 16-byte file header, then one DEM_Packet frame per payload
    ///     (ULEB128 command, tick, size). No DEM_Stop — the scan simply runs off the end.
    /// </summary>
    private static byte[] BuildDemo(params byte[][] framePayloads)
    {
        List<byte> file = [.. Encoding.ASCII.GetBytes("PBDEMS2"), 0];
        file.AddRange(new byte[8]); // two int32LE header fields, unread by the parser

        foreach (byte[] payload in framePayloads)
        {
            WriteVarint(file, (uint)EDemoCommands.DemPacket);
            WriteVarint(file, 0); // tick
            WriteVarint(file, (uint)payload.Length);
            file.AddRange(payload);
        }

        return file.ToArray();
    }

    private static void WriteVarint(List<byte> into, uint value)
    {
        while (value >= 0x80)
        {
            into.Add((byte)(value & 0x7F | 0x80));
            value >>= 7;
        }

        into.Add((byte)value);
    }

    /// <summary>
    ///     Minimal bit writer, LSB-first to match <see cref="BitBuffer" />'s read order (the same
    ///     shape <c>StringTableBoundsTests</c> uses), plus the UBitVar encoding the inner-message
    ///     type id needs. <c>UBitVarRoundTripsThroughBitBuffer</c> below is what makes it trustworthy.
    /// </summary>
    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _bitPos;

        public Bits One(bool value = true)
        {
            if (_bitPos == 0)
            {
                _bytes.Add(0);
            }

            if (value)
            {
                _bytes[^1] |= (byte)(1 << _bitPos);
            }

            _bitPos = (_bitPos + 1) % 8;
            return this;
        }

        /// <summary>Writes the low <paramref name="count" /> bits of <paramref name="value" />, LSB first.</summary>
        public Bits Raw(uint value, int count)
        {
            for (int i = 0; i < count; i++)
            {
                One((value & (1u << i)) != 0);
            }

            return this;
        }

        /// <summary>Writes a protobuf-style unsigned varint (byte-sized groups, not byte-aligned).</summary>
        public Bits VarInt(uint value)
        {
            while (value >= 0x80)
            {
                Raw(value & 0x7F | 0x80, 8);
                value >>= 7;
            }

            return Raw(value, 8);
        }

        /// <summary>
        ///     Writes Source's UBitVar: a 6-bit head whose top two bits select how many further bits
        ///     carry the value's high part (none / 4 / 8 / 28).
        /// </summary>
        public Bits UBitVar(uint value)
        {
            if (value < 16)
            {
                return Raw(value, 6);
            }

            if (value < 1 << 8)
            {
                return Raw(value & 15 | 16, 6).Raw(value >> 4, 4);
            }

            if (value < 1 << 12)
            {
                return Raw(value & 15 | 32, 6).Raw(value >> 4, 8);
            }

            return Raw(value & 15 | 48, 6).Raw(value >> 4, 28);
        }

        public Bits Append(Bits other)
        {
            // Bit-exact concatenation: byte-appending would silently pad at every message boundary.
            byte[] src = other.ToArray();
            int bits = other.BitLength;
            for (int i = 0; i < bits; i++)
            {
                One((src[i / 8] & (1 << i % 8)) != 0);
            }

            return this;
        }

        public int BitLength => _bytes.Count == 0 ? 0 : (_bytes.Count - 1) * 8 + (_bitPos == 0 ? 8 : _bitPos);

        public byte[] ToArray() => _bytes.ToArray();
    }

    /// <summary>
    ///     The writer above is only as trustworthy as its agreement with the real reader — so pin it
    ///     by round-tripping the exact header shape the inner-message loop reads, across every
    ///     UBitVar width class.
    /// </summary>
    [Test]
    [Arguments(0u)]
    [Arguments(15u)]
    [Arguments(16u)]
    [Arguments(200u)]
    [Arguments(255u)]
    [Arguments(256u)]
    [Arguments(4095u)]
    [Arguments(4096u)]
    [Arguments(70000u)]
    public async Task UBitVarRoundTripsThroughBitBuffer(uint value)
    {
        byte[] encoded = new Bits().UBitVar(value).VarInt(300).Raw(0xAB, 8).ToArray();

        BitBuffer buf = new(encoded);
        uint readTypeId = buf.ReadUBitVar();
        uint readSize = buf.ReadUVarInt32();
        byte readPayload = buf.ReadByte();

        await Assert.That(readTypeId).IsEqualTo(value);
        await Assert.That(readSize).IsEqualTo(300u);
        await Assert.That(readPayload).IsEqualTo((byte)0xAB);
    }

    /// <summary><see cref="IProgress{T}" /> over a plain delegate — reports synchronously, on the reporting thread.</summary>
    private sealed class CallbackProgress(Action<double> onReport) : IProgress<double>
    {
        public void Report(double value) => onReport(value);
    }

    /// <summary>
    ///     Records the peak number of pass-2 workers ever inside the progress callback at once.
    ///     Holds only for the first <paramref name="holdCount" /> reports so a capped run stays cheap.
    /// </summary>
    private sealed class ConcurrencyProbe(TimeSpan hold, int holdCount) : IProgress<double>
    {
        private readonly Lock _gate = new();
        private int _active;

        public int PeakConcurrency { get; private set; }

        public int Reports { get; private set; }

        public void Report(double value)
        {
            bool shouldHold;
            lock (_gate)
            {
                _active++;
                Reports++;
                shouldHold = Reports <= holdCount;
                PeakConcurrency = Math.Max(PeakConcurrency, _active);
            }

            if (shouldHold)
            {
                Thread.Sleep(hold);
            }

            lock (_gate)
            {
                _active--;
            }
        }
    }
}
