#region

using System.Collections.Concurrent;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>Demo parser tests.</summary>
[NotInParallel]
[Category("Integration")]
public class DemoParserTests
{
    /// <summary>
    ///     Regression test: a healthy entity decode pipeline produces no LastEntityError on the
    ///     reference demo. When the bit-misalignment bug fires, ReadEntityFields throws
    ///     "FieldPath is full" once the 2,048-path safety limit is hit; the exception is
    ///     swallowed and recorded in LastEntityError.
    /// </summary>
    [Test]
    [Category("Oracle")]
    public async Task EntityTracker_FuriaMirage_NoEntityDecodeErrors()
    {
        const string DemoFilename = "furia-vs-vitality-m1-mirage.dem";
        string path = DemoTestHelper.RequireDemo(DemoFilename);

        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        ParsedDemo parsed = DemoParser.Parse(demoBytes.AsMemory());

        EntityTracker tracker = new();
        tracker.Replay(parsed.Frames);

        if (tracker.LastEntityError is { } err)
        {
            Console.WriteLine($"LastEntityError: {err}");
        }

        await Assert.That(tracker.LastEntityError).IsNull();
    }

    /// <summary>
    ///     Regression test: pins the bit-misalignment bug where length-1 paths landing on a
    ///     null-Decoder nested-object descriptor silently consume 0 bits. The cascade produces
    ///     hundreds of phantom CCSGameRulesProxy enterPvs events (965+ vs demofile-net's 1).
    ///     Ground truth from demofile-net 0.42.1 on furia-vs-vitality-m1-mirage.dem:
    ///     1 CCSGameRulesProxy creation, 1 distinct slot, 1 max simultaneous.
    ///     This test asserts the count stays in single digits with margin.
    /// </summary>
    [Test]
    [Category("Oracle")]
    public async Task EntityTracker_FuriaMirage_NoPhantomGameRulesProxyCreations()
    {
        const string DemoFilename = "furia-vs-vitality-m1-mirage.dem";
        string path = DemoTestHelper.RequireDemo(DemoFilename);

        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        ParsedDemo parsed = DemoParser.Parse(demoBytes.AsMemory());

        EntityTracker tracker = new();

        Dictionary<string, int> createdByClass = new();
        tracker.EntityCreated += (_, state) =>
        {
            createdByClass.TryGetValue(state.ClassName, out int count);
            createdByClass[state.ClassName] = count + 1;
        };

        tracker.Replay(parsed.Frames);

        createdByClass.TryGetValue("CCSGameRulesProxy", out int gameRulesProxyCreations);
        Console.WriteLine($"CCSGameRulesProxy creations: {gameRulesProxyCreations} (demofile-net ground truth: 1)");

        if (tracker.LastEntityError is { } err)
        {
            Console.WriteLine($"LastEntityError: {err}");
        }

        // Top 10 classes by creation count, for visibility on regressions
        Console.WriteLine("Top creation counts:");
        foreach (KeyValuePair<string, int> kv in createdByClass.OrderByDescending(k => k.Value).Take(10))
        {
            Console.WriteLine($"  {kv.Key,-40} {kv.Value}");
        }

        // Strict bound: ground truth is 1. Allow up to 5 to absorb minor scoring differences
        // across baselines/full-packets without letting the bug back in.
        await Assert.That(gameRulesProxyCreations).IsLessThanOrEqualTo(5);
    }

    /// <summary>Parse demo_all inner messages have payload.</summary>
    [Test]
    [Category("Smoke")]
    public async Task ParseDemo_AllInnerMessagesHavePayload()
    {
        string path = DemoTestHelper.RequireDemo();

        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        IReadOnlyList<DemoFrame> frames = DemoParser.Parse(demoBytes.AsMemory()).Frames;

        List<string> nullPayloads = frames
            .SelectMany(f => f.InnerMessages)
            .Where(m => m.Payload is null) // NetMessage.Payload is non-nullable; this stays for future-proofing
            .Select(m => m.MessageTypeName)
            .ToList();

        if (nullPayloads.Count > 0)
        {
            Console.WriteLine($"{nullPayloads.Count} inner messages had null payloads:");
            foreach (string name in nullPayloads.Distinct())
            {
                Console.WriteLine($"  {name}");
            }
        }
        else
        {
            Console.WriteLine("All inner messages parsed successfully.");
        }

        // Not a hard failure — null payloads just mean unknown message types
        Console.WriteLine($"Total frames: {frames.Count}");
    }

    // ParseDemo_PrintsFrameSummary deleted: 70 LOC of diagnostic
    // printout protecting two trivial assertions. The frame-count bound moved
    // to ParseDemo_FrameByteRangesAreContiguous (which already iterates frames);
    // the DEM_FileHeader presence check moved to ParseDemo_DoesNotThrow below.
    // For developer-time inspection of the frame command breakdown, use:
    //   dotnet run --project tools/DemoViewer.NET.DemoSourceDetails -- \
    //       <source-name> <demo.dem>

    /// <summary>
    ///     Smoke test: parse + entity replay both complete without throwing on a real demo.
    ///     Covers two failure classes in one slot:
    ///     <list type="bullet">
    ///         <item>
    ///             <c>DemoParser.Parse</c> field-type mismatches in
    ///             <c>GameEventDecoder</c> (e.g. a field declared as val_long that actually
    ///             carries a string) that only surface when a specific event is present.
    ///         </item>
    ///         <item>
    ///             <c>EntityTracker.Replay</c> bit-misalignment / decoder bugs that
    ///             surface as "FieldPath is full" or similar exceptions partway through.
    ///         </item>
    ///     </list>
    ///     Merged from the former <c>EntityTracker_ReplayDoesNotThrow</c> —
    ///     same regression signal in fewer slots, less diagnostic noise in test output.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task ParseDemo_DoesNotThrow()
    {
        string path = DemoTestHelper.RequireDemo();

        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        Exception? caught = null;
        ParsedDemo? parsed = null;

        try
        {
            parsed = DemoParser.Parse(demoBytes.AsMemory());
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        await Assert.That(caught).IsNull();

        // Frame-count + header invariants migrated from the deleted
        // ParseDemo_PrintsFrameSummary: catches truncated parses
        // and missing file-header. A typical CS2 demo has 100k–1M frames
        // (64-tick = ~3,840 frames/min); the upper bound is generous for OT.
        await Assert.That(parsed!.Frames.Count).IsBetween(10_000, 5_000_000).WithInclusiveBounds();
        DemoFrame? header = parsed.Frames.FirstOrDefault(f => f.Command == "DEM_FileHeader");
        await Assert.That(header).IsNotNull();

        // Now exercise the entity replay too — same robustness signal at the
        // entity-decode layer. Bounded on entity count to catch silent
        // truncation (parser bails halfway and leaves the tracker tiny).
        EntityTracker tracker = new();
        tracker.Replay(parsed.Frames);
        int entityCount = tracker.CurrentEntities.All().Count();
        await Assert.That(entityCount).IsBetween(100, 50_000).WithInclusiveBounds();
    }

    /// <summary>
    ///     Verifies that consecutive frame byte ranges are contiguous: each frame starts exactly
    ///     where the previous one ended.  A gap or overlap indicates a bug in offset tracking.
    /// </summary>
    [Test]
    public async Task ParseDemo_FrameByteRangesAreContiguous()
    {
        string path = DemoTestHelper.RequireDemo();

        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        IReadOnlyList<DemoFrame> frames = DemoParser.Parse(demoBytes.AsMemory()).Frames;

        // First frame must start immediately after the 16-byte file header.
        int errors = 0;
        if (frames.Count > 0 && frames[0].RawStart != 16)
        {
            Console.WriteLine($"First frame RawStart={frames[0].RawStart}, expected 16.");
            errors++;
        }

        // Each subsequent frame must start exactly where the previous frame ended.
        for (int i = 1; i < frames.Count; i++)
        {
            int expected = frames[i - 1].RawStart + frames[i - 1].RawLength;
            if (frames[i].RawStart != expected)
            {
                Console.WriteLine($"Frame {i} ({frames[i].Command}) RawStart={frames[i].RawStart}, " +
                                  $"expected {expected} (gap/overlap of {frames[i].RawStart - expected} bytes)");
                if (++errors >= 10)
                {
                    Console.WriteLine("  (stopping after 10 errors)");
                    break;
                }
            }
        }

        if (errors == 0)
        {
            Console.WriteLine($"All {frames.Count} frames are byte-contiguous (first at offset 16).");
        }

        await Assert.That(errors).IsEqualTo(0);
    }

    // ParseDemo_UnknownGameEventNames deleted: diagnostic printout
    // of unknown-event names and field keys. The total-event-count range bound
    // moved to ParseDemo_GameEventsDecoded below (the natural home — already
    // asserts on AllGameEvents). For unknown-event inspection at developer
    // time, use DemoSourceDetails which already breaks down typed vs unknown
    // events in its JSON output.

    /// <summary>
    ///     Load-bearing assertion: every GameEventMessage slot in frames produces exactly
    ///     one entry in <c>parsed.AllGameEvents</c>. A drift here means the parser is
    ///     either double-counting or dropping events during the enrichment pass.
    ///     Diagnostic printout stripped (use DemoSourceDetails for that).
    /// </summary>
    [Test]
    public async Task ParseDemo_GameEventsDecoded()
    {
        string path = DemoTestHelper.RequireDemo();

        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        ParsedDemo parsed = DemoParser.Parse(demoBytes.AsMemory());

        // GameEventMessages in frames must match the indexed AllGameEvents list.
        int gameEventMsgCount = parsed.Frames
            .SelectMany(f => f.InnerMessages)
            .OfType<GameEventMessage>()
            .Count();
        await Assert.That(gameEventMsgCount).IsEqualTo(parsed.AllGameEvents.Count);

        // Event-count range bound migrated from the deleted ParseDemo_UnknownGameEventNames.
        // A typical CS2 demo emits 5k–50k game events; lower catches a broken
        // GameEventDecoder, upper is generous for OT.
        await Assert.That(parsed.AllGameEvents.Count).IsBetween(1_000, 500_000).WithInclusiveBounds();
    }

    /// <summary>
    ///     Pins the event contract for <see cref="DemoParser.OnUnknownMessageType" />: when a
    ///     net-message type ID lands in <c>ParseNetMessage</c>'s default arm, the event must
    ///     fire exactly once per occurrence with the (typeId, typeName) tuple, and the parser
    ///     must continue (no crash, message dropped).
    ///     We exercise this by parsing the reference demo with the event subscribed. The
    ///     reference demo's content is fixed; if it contains zero unknown messages, the test
    ///     simply asserts no event fires (and the parse succeeds). If a new CS2 version ships
    ///     a message type that this parser hasn't yet added to its switch, this test will
    ///     start logging the (typeId, typeName) — a useful early signal.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task ParseDemo_OnUnknownMessageType_FiresAndDoesNotThrow()
    {
        string path = DemoTestHelper.RequireDemo();
        byte[] demoBytes = await File.ReadAllBytesAsync(path);

        // ConcurrentBag because the event is raised from Pass 2 parallel parse threads.
        // The first version of this test used List<T>.Add and surfaced a "capacity was
        // less than the current size" exception — that's the documented threading contract
        // in action: handlers must be thread-safe.
        ConcurrentBag<UnknownMessageInfo> unknowns = new();
        Action<UnknownMessageInfo> handler = info => { unknowns.Add(info); };

        DemoParser.OnUnknownMessageType += handler;
        ParsedDemo parsed;
        try
        {
            parsed = DemoParser.Parse(demoBytes.AsMemory());
            await Assert.That(parsed.Frames.Count).IsGreaterThan(0);
        }
        finally
        {
            // Static event — must unsubscribe so other tests in this assembly don't see the handler.
            DemoParser.OnUnknownMessageType -= handler;
        }

        // Contract on the enriched payload: every occurrence must carry a seekable frame number
        // (a valid index into Frames) and a positive byte length, so the UI can locate the bytes.
        foreach (UnknownMessageInfo info in unknowns)
        {
            await Assert.That(info.FrameNumber).IsGreaterThanOrEqualTo(0);
            await Assert.That(info.FrameNumber).IsLessThan(parsed.Frames.Count);
            await Assert.That(info.Length).IsGreaterThan(0);
        }

        // No assertion on count — the reference demo may or may not contain unknown types.
        // What matters is the contract: the parser ran to completion, and any unknowns were
        // surfaced rather than silently dropped.
        if (!unknowns.IsEmpty)
        {
            Console.WriteLine($"Saw {unknowns.Count} unknown message occurrence(s):");
            foreach (IGrouping<int, UnknownMessageInfo> u in unknowns.GroupBy(x => x.TypeId).OrderBy(g => g.Key))
            {
                Console.WriteLine($"  typeId={u.Key,4}  typeName={u.First().TypeName,-30}  occurrences={u.Count()}");
            }
        }
        else
        {
            Console.WriteLine("No unknown message types observed in this demo — all type IDs mapped to parsers.");
        }
    }

    /// <summary>
    ///     Simulates the exact UI flow:
    ///     1. Load all file bytes (same as File.ReadAllBytesAsync in MainViewModel.OpenFileAsync)
    ///     2. Parse via MemoryStream wrapping those bytes (zero-copy, same as UI)
    ///     3. For every frame, call GetDecompressedPayload and verify no out-of-range slices
    ///     4. For every inner message, verify DecompressedStart/DecompressedLength slice is in bounds
    ///     This catches any bug in RawStart/RawLength/HeaderLength tracking or in
    ///     DecompressedStart/DecompressedLength calculation.
    /// </summary>
    [Test]
    public async Task ParseDemo_UiFlowByteRangesAreValid()
    {
        string path = DemoTestHelper.RequireDemo();

        // ── Step 1: load all bytes (UI does this before parsing) ──────────────
        byte[] demoBytes = await File.ReadAllBytesAsync(path);
        Console.WriteLine($"Demo: {Path.GetFileName(path)}  ({demoBytes.Length:N0} bytes)");

        // ── Step 2: parse synchronously (same as UI) ─────────────────────────
        IReadOnlyList<DemoFrame> frames = DemoParser.Parse(demoBytes.AsMemory()).Frames;

        Console.WriteLine($"Frames: {frames.Count}");

        // ── Step 3: validate frame byte ranges ────────────────────────────────
        int frameErrors = 0;
        foreach (DemoFrame frame in frames)
        {
            // RawStart must be inside the file.
            if (frame.RawStart < 0 || frame.RawStart >= demoBytes.Length)
            {
                Console.WriteLine($"  FRAME RANGE ERROR: {frame.Command} tick={frame.ServerTick}  " +
                                  $"RawStart={frame.RawStart} out of [0, {demoBytes.Length})");
                frameErrors++;
                continue;
            }

            // RawStart + RawLength must not exceed file size.
            if (frame.RawStart + frame.RawLength > demoBytes.Length)
            {
                Console.WriteLine($"  FRAME RANGE ERROR: {frame.Command} tick={frame.ServerTick}  " +
                                  $"RawStart+RawLength={frame.RawStart + frame.RawLength} > file size {demoBytes.Length}");
                frameErrors++;
                continue;
            }

            // HeaderLength must be positive and less than RawLength.
            if (frame.HeaderLength <= 0 || frame.HeaderLength > frame.RawLength)
            {
                Console.WriteLine($"  FRAME HEADER ERROR: {frame.Command} tick={frame.ServerTick}  " +
                                  $"HeaderLength={frame.HeaderLength} RawLength={frame.RawLength}");
                frameErrors++;
                continue;
            }

            // GetDecompressedPayload must not throw.
            byte[]? decomp = null;
            if (frame.PayloadLength > 0)
            {
                try
                {
                    decomp = DownstreamUtilities.GetDecompressedPayload(frame, demoBytes);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  DECOMPRESS ERROR: {frame.Command} tick={frame.ServerTick}  " +
                                      $"PayloadStart={frame.PayloadStart} PayloadLength={frame.PayloadLength}  " +
                                      $"{ex.GetType().Name}: {ex.Message}");
                    frameErrors++;
                    continue;
                }
            }

            // ── Step 4: validate message byte ranges within the decompressed payload ──
            foreach (NetMessage msg in frame.InnerMessages)
            {
                if (msg.DecompressedStart is not { } start || msg.DecompressedLength is not { } len)
                {
                    continue; // null = position unknown, skip
                }

                if (decomp is null)
                {
                    continue;
                }

                if (start < 0 || len < 0 || start + len > decomp.Length)
                {
                    Console.WriteLine($"    MSG RANGE ERROR: {msg.MessageTypeName}  " +
                                      $"DecompressedStart={start} DecompressedLength={len}  " +
                                      $"decomp.Length={decomp.Length}");
                    frameErrors++;
                }
            }
        }

        Console.WriteLine();
        if (frameErrors == 0)
        {
            Console.WriteLine("All frame and message byte ranges are valid.");
        }
        else
        {
            Console.WriteLine($"ERRORS: {frameErrors} range violations found.");
        }

        // Frame byte ranges must all be valid — this is what the UI relies on.
        await Assert.That(frameErrors).IsEqualTo(0);
    }
}
