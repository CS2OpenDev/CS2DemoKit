#region

using System.Diagnostics;
using System.Globalization;
using System.Text;
using CS2DemoKit.Analysis;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.Entities;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.Parser.GameEvents;
using CS2OpenSchema.Protos;

#endregion

namespace CS2DemoKit.Bench;

/// <summary>
///     One arm of the paths benchmark over one demo, in a fresh process, emitted as a CSV row.
///     <para>
///         Each arm is one way of consuming a demo, from reading the file to running the shipped
///         rulesets, on the forward reader and on the retained parse. What the row records is the
///         arm's wall-clock, allocation and collector cost, and the process's memory high-water
///         mark sampled while it ran, so a path's memory can be plotted against the demo's size.
///         Every arm also folds what it produced into a digest; the orchestrator refuses a pair of
///         rows whose two paths disagree, so a flat memory line can never be bought with a
///         different answer.
///     </para>
///     <para>
///         No warm-up pass: a discarded pipeline would leave its heap committed and the sampler
///         would report that instead of the arm. Wall-clock therefore includes JIT, the same on
///         every demo and every arm, and this row's timings are for comparing arms against each
///         other and against demo size, not against <see cref="Measurement" />'s.
///     </para>
/// </summary>
internal static class PathMeasurement
{
    public const string FileRead = "file-read";
    public const string MessageScan = "message-scan";
    public const string GameEvents = "game-events";
    public const string EntityReplay = "entity-replay";
    public const string MaterialisedReplay = "materialised-replay";
    public const string ScoreboardMaterialised = "scoreboard-materialised";
    public const string ScoreboardStream = "scoreboard-stream";
    public const string TrackerPrime = "tracker-prime";
    public const string Parse = "parse";
    public const string Materialise = "materialise";

    public static readonly IReadOnlyList<string> AllArms =
    [
        FileRead, MessageScan, GameEvents, EntityReplay, MaterialisedReplay, ScoreboardMaterialised, ScoreboardStream, TrackerPrime,
        Parse, Materialise
    ];

    /// <summary>Arm pairs that must produce the same digest on the same demo.</summary>
    public static readonly IReadOnlyList<(string A, string B)> DigestPairs =
    [
        (EntityReplay, MaterialisedReplay),
        (ScoreboardStream, ScoreboardMaterialised),
        (Parse, Materialise)
    ];

    // The reader sizes its window arrays up front, so a whole-file window has to be a number.
    // Above every frame count in the corpus (228,902), so each demo decodes in one window.
    private const int WholeFileWindow = 1 << 18;

    public const string Header =
        "variant,arm,demo,size_mb,run,wall_ms,alloc_mb,pause_ms,gen0,gen1,gen2,"
        + "base_heap_mb,peak_heap_mb,base_committed_mb,peak_committed_mb,base_working_set_mb,peak_working_set_mb,"
        + "peak_private_mb,samples,frames,messages,digest,load1";

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly record struct ArmResult(long Frames, long Messages, ulong Digest);

    public static string Run(string demoPath, string arm, string variant, string runIndex)
    {
        Func<string, ArmResult> body = arm switch
        {
            FileRead => RunFileRead,
            MessageScan => RunMessageScan,
            GameEvents => RunGameEvents,
            EntityReplay => RunEntityReplay,
            MaterialisedReplay => RunMaterialisedReplay,
            ScoreboardMaterialised => RunScoreboardMaterialised,
            ScoreboardStream => RunScoreboardStream,
            TrackerPrime => RunTrackerPrime,
            Parse => RunParse,
            Materialise => RunMaterialise,
            _ => throw new ArgumentException($"unknown arm {arm}", nameof(arm))
        };

        long sizeBytes = new FileInfo(demoPath).Length;

        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        long allocBefore = GC.GetTotalAllocatedBytes();
        TimeSpan pauseBefore = GC.GetTotalPauseDuration();
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);

        using AllocationTicks? ticks = AllocationTicks.StartIfRequested();
        MemorySampler sampler = MemorySampler.Start();
        long t = Stopwatch.GetTimestamp();
        ArmResult result = body(demoPath);
        double wallMs = (Stopwatch.GetTimestamp() - t) * 1000.0 / Stopwatch.Frequency;
        sampler.Stop();
        ticks?.Report(Console.Error);

        double pauseMs = (GC.GetTotalPauseDuration() - pauseBefore).TotalMilliseconds;
        long alloc = GC.GetTotalAllocatedBytes() - allocBefore;

        const double MB = 1024.0 * 1024;
        return string.Join(",",
            variant, arm, Path.GetFileName(demoPath),
            (sizeBytes / MB).ToString("F1", CultureInfo.InvariantCulture),
            runIndex,
            wallMs.ToString("F2", CultureInfo.InvariantCulture),
            (alloc / MB).ToString("F1", CultureInfo.InvariantCulture),
            pauseMs.ToString("F2", CultureInfo.InvariantCulture),
            GC.CollectionCount(0) - g0, GC.CollectionCount(1) - g1, GC.CollectionCount(2) - g2,
            (sampler.BaseHeap / MB).ToString("F1", CultureInfo.InvariantCulture),
            (sampler.PeakHeap / MB).ToString("F1", CultureInfo.InvariantCulture),
            (sampler.BaseCommitted / MB).ToString("F1", CultureInfo.InvariantCulture),
            (sampler.PeakCommitted / MB).ToString("F1", CultureInfo.InvariantCulture),
            (sampler.BaseWorkingSet / MB).ToString("F1", CultureInfo.InvariantCulture),
            (sampler.PeakWorkingSet / MB).ToString("F1", CultureInfo.InvariantCulture),
            (sampler.PeakPrivate / MB).ToString("F1", CultureInfo.InvariantCulture),
            sampler.Samples,
            result.Frames, result.Messages,
            result.Digest.ToString("X16", CultureInfo.InvariantCulture));
    }

    // Reading the file is the floor every other arm sits on. The bytes are folded so the read
    // cannot be elided, and the digest is of the file itself.
    private static ArmResult RunFileRead(string demoPath)
    {
        byte[] bytes = File.ReadAllBytes(demoPath);
        ulong h = FnvOffset;
        foreach (byte b in bytes)
        {
            h = (h ^ b) * FnvPrime;
        }

        return new ArmResult(0, bytes.Length, h);
    }

    // Every frame header and every inner message header, nothing decoded. The digest is the
    // per-type-id message count, which is the question a structure pass exists to answer.
    private static ArmResult RunMessageScan(string demoPath)
    {
        using DemoReader reader = DemoReader.OpenFile(demoPath, new ParseOptions { Plan = DecodePlan.StructureOnly, ReadAheadFrames = ReadAhead });
        Dictionary<int, long> counts = [];
        long frames = 0, messages = 0;
        foreach (DemoFrame frame in reader.ReadFrames())
        {
            frames++;
            ReadOnlySpan<InnerMessageHeader> headers = frame.InnerMessageHeaders.Span;
            messages += headers.Length;
            foreach (InnerMessageHeader h in headers)
            {
                counts[h.TypeId] = counts.GetValueOrDefault(h.TypeId) + 1;
            }
        }

        ulong digest = FnvOffset;
        foreach (int id in counts.Keys.Order())
        {
            digest = Fold(digest, id);
            digest = Fold(digest, counts[id]);
        }

        return new ArmResult(frames, messages, digest);
    }

    // Typed game events only, in order, digested by tick and name.
    private static ArmResult RunGameEvents(string demoPath)
    {
        using DemoReader reader = DemoReader.OpenFile(demoPath, new ParseOptions { Plan = DecodePlan.GameEventsOnly, ReadAheadFrames = ReadAhead });
        long frames = 0, events = 0;
        ulong digest = FnvOffset;
        foreach (DemoFrame frame in reader.ReadFrames())
        {
            frames++;
            foreach (NetMessage msg in frame.DecodedMessages)
            {
                if (msg is GameEventMessage gem)
                {
                    events++;
                    digest = Fold(digest, frame.ServerTick);
                    digest = Fold(digest, gem.DecodedEvent.Name);
                }
            }
        }

        return new ArmResult(frames, events, digest);
    }

    // The curated tracker advanced one frame at a time off the reader, digested at every full
    // packet and at the end. Nothing but the tracker and the current frame is alive.
    private static ArmResult RunEntityReplay(string demoPath)
    {
        using DemoReader reader = DemoReader.OpenFile(demoPath, new ParseOptions { Plan = DecodePlan.EntityReplay, ReadAheadFrames = ReadAhead });
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        bool track = Environment.GetEnvironmentVariable("CS2DEMOKIT_PATHS_NOTRACK") != "1";
        long frames = 0, packets = 0;
        ulong digest = FnvOffset;
        foreach (DemoFrame frame in reader.ReadFrames())
        {
            frames++;
            if (track)
            {
                tracker.AdvanceOneFrame(frame);
            }

            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                packets++;
                digest = Fold(digest, EntitySetDigest.Compute(tracker.CurrentEntities));
            }
        }

        digest = Fold(digest, EntitySetDigest.Compute(tracker.CurrentEntities));
        return new ArmResult(frames, packets, digest);
    }

    // The control: the same tracker walk over a retained parse, so the digest has to match
    // the reader arm's and the memory difference is the retention.
    private static ArmResult RunMaterialisedReplay(string demoPath)
    {
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        EntityTracker tracker = EntityTrackerFactory.CreateCurated();
        long packets = 0;
        ulong digest = FnvOffset;
        foreach (DemoFrame frame in demo.Frames)
        {
            tracker.AdvanceOneFrame(frame);
            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                packets++;
                digest = Fold(digest, EntitySetDigest.Compute(tracker.CurrentEntities));
            }
        }

        digest = Fold(digest, EntitySetDigest.Compute(tracker.CurrentEntities));
        GC.KeepAlive(demo);
        return new ArmResult(demo.Frames.Count, packets, digest);
    }

    // The shipped rulesets over a retained demo, snapshots on: what a viewer does today. The
    // parse takes no cap, so the DOP knob moves only the digest workers.
    private static ArmResult RunScoreboardMaterialised(string demoPath)
    {
        RuleConfigLoadResult rules = LoadRules();
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory());
        AnalysisRun run = DemoAnalysis.Run(demo, rules.Rulesets, new AnalysisOptions { MaxDegreeOfParallelism = Dop });
        ArmResult result = new(run.Provenance.FramesConsumed, run.Provenance.MessagesConsumed, ScoreboardDigest(run));
        GC.KeepAlive(demo);
        return result;
    }

    // The shipped rulesets straight off the file, snapshots off: the forward path. The
    // parallelism cap is the one knob worth sweeping here, so the environment may set it.
    private static ArmResult RunScoreboardStream(string demoPath)
    {
        RuleConfigLoadResult rules = LoadRules();
        AnalysisOptions options = new()
        {
            MaxDegreeOfParallelism = Dop,
            ProbeDialect = Environment.GetEnvironmentVariable("CS2DEMOKIT_PATHS_PROBE") != "0"
        };
        AnalysisRun run;
        if (ReadAhead > 0)
        {
            using DemoReader reader = DemoReader.OpenFile(demoPath,
                new ParseOptions { ReadAheadFrames = ReadAhead, MaxDegreeOfParallelism = options.MaxDegreeOfParallelism });
            run = DemoAnalysis.Run(reader, rules.Rulesets, options);
        }
        else
        {
            run = DemoAnalysis.Run(demoPath, rules.Rulesets, options);
        }

        return new ArmResult(run.Provenance.FramesConsumed, run.Provenance.MessagesConsumed, ScoreboardDigest(run));
    }

    // One digest worker's fixed cost: a curated tracker primed from the signon prefix and the
    // first checkpoint. Reports the live bytes it holds afterwards in the messages column.
    private static ArmResult RunTrackerPrime(string demoPath)
    {
        using DemoReader reader = DemoReader.OpenFile(demoPath,
            new ParseOptions { Plan = DecodePlan.EntityReplay with { RetainSignonPrefix = true } });
        DemoFrame? checkpoint = null, successor = null, instanceBaseline = null;
        bool seenFirst = false;
        long frames = 0;
        foreach (DemoFrame frame in reader.ReadFrames())
        {
            frames++;
            if (checkpoint is not null)
            {
                successor = frame;
                break;
            }

            if (frame.CommandKind == EDemoCommands.DemFullPacket)
            {
                if (seenFirst)
                {
                    checkpoint = frame;
                    instanceBaseline = reader.LastInstanceBaselineFullPacket;
                }

                seenFirst = true;
            }
        }

        if (checkpoint is null)
        {
            throw new InvalidOperationException("no checkpoint full packet in the demo");
        }

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long before = GC.GetTotalMemory(true);
        EntityStateLayer layer = new();
        layer.PrimeFromCheckpoint(reader.SignonPrefix, instanceBaseline, checkpoint,
            successor is not null && successor.ServerTick == checkpoint.ServerTick ? null : successor);
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long after = GC.GetTotalMemory(true);
        ulong digest = EntitySetDigest.Compute(layer.Tracker.CurrentEntities);
        GC.KeepAlive(layer);
        return new ArmResult(frames, after - before, digest);
    }

    // The whole-file parse as a consumer calls it: three passes over the file's bytes, every
    // frame retained. Digested afterwards, so the digest walk costs the same on both loops.
    private static ArmResult RunParse(string demoPath)
    {
        ParsedDemo demo = DemoParser.Parse(File.ReadAllBytes(demoPath).AsMemory(), new ParseOptions());
        ArmResult result = DecodeDigest(demo.Frames);
        GC.KeepAlive(demo);
        return result;
    }

    // The reader's windowed loop with one window over the whole file, every frame retained.
    // Materialize() as shipped delegates to DemoParser.Parse, so the reader itself is walked.
    private static ArmResult RunMaterialise(string demoPath)
    {
        int window = ReadAhead > 0 ? ReadAhead : WholeFileWindow;
        using DemoReader reader = DemoReader.OpenFile(demoPath, new ParseOptions { ReadAheadFrames = window });
        List<DemoFrame> frames = [];
        foreach (DemoFrame frame in reader.ReadFrames())
        {
            frames.Add(frame);
        }

        ArmResult result = DecodeDigest(frames);
        GC.KeepAlive(frames);
        return result;
    }

    // Frame count, then per frame the command, the tick and each decoded message's type id;
    // the name where the catalog has no id, which is the direct-payload commands.
    private static ArmResult DecodeDigest(IReadOnlyList<DemoFrame> frames)
    {
        ulong digest = Fold(FnvOffset, (long)frames.Count);
        long messages = 0;
        foreach (DemoFrame frame in frames)
        {
            digest = Fold(digest, (long)frame.CommandKind);
            digest = Fold(digest, frame.ServerTick);
            IReadOnlyList<NetMessage> decoded = frame.DecodedMessages;
            messages += decoded.Count;
            foreach (NetMessage msg in decoded)
            {
                digest = NetMessageCatalog.TryGetTypeId(msg.MessageTypeName, out int id)
                    ? Fold(digest, id)
                    : Fold(digest, msg.MessageTypeName);
            }
        }

        return new ArmResult(frames.Count, messages, digest);
    }

    private static int ReadAhead => int.TryParse(Environment.GetEnvironmentVariable("CS2DEMOKIT_PATHS_READAHEAD"), out int n) ? n : 0;

    private static int? Dop => int.TryParse(Environment.GetEnvironmentVariable("CS2DEMOKIT_PATHS_DOP"), out int dop) ? dop : null;

    private static RuleConfigLoadResult LoadRules()
    {
        RuleConfigLoadResult rules = YamlConfigLoader.LoadShippedEmbedded();
        if (!rules.Success)
        {
            throw new InvalidOperationException(
                "shipped rulesets failed to load: " + string.Join("; ", rules.Errors.Select(e => e.Message)));
        }

        return rules;
    }

    /// <summary>
    ///     Everything a run produces in both capture modes, sorted, with each materialised
    ///     player's name replaced by a slot token: the stream resolves a name when the slot first
    ///     materialises and the retained parse resolves the final one, and a mid-match rename is
    ///     not a difference in the analysis. Configured tables are left out because they need
    ///     snapshot mode, which the stream arm deliberately runs without.
    /// </summary>
    private static ulong ScoreboardDigest(AnalysisRun run)
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

        List<string> lines = new(run.Timeline.Events.Count + run.Highlights.Count + run.FinalNodes.Count + 4);
        lines.Add($"highlights={run.Highlights.Count}");
        lines.Add($"ruleChainEvents={run.Timeline.Events.Count}");
        lines.Add($"materializedSlots={string.Join(",", run.MaterializedPlayers.Select(mp => mp.PlayerSlot).Order())}");
        foreach (var e in run.Timeline.Events)
        {
            lines.Add($"chain|{e.ChainName}|{e.Tick}|{e.FrameIndex}|{e.PlayerSlot?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        }

        foreach (HighlightFired h in run.Highlights)
        {
            lines.Add($"hl|{h.RulesetId}/{h.HighlightId}|{h.Tick}|{h.PlayerSlot}|{h.RoundNumber}|{Normalise(h.RenderedTitle)}");
        }

        foreach (var n in run.FinalNodes)
        {
            lines.Add($"node|{n.Name}|{Normalise(n.Subtitle)}|{n.IsActive}|{Normalise(n.GetDisplayValue())}|"
                      + (n.GetNumericValue() is { } f ? f.ToString("F6", CultureInfo.InvariantCulture) : "-"));
        }

        lines.Sort(StringComparer.Ordinal);
        ulong digest = FnvOffset;
        foreach (string line in lines)
        {
            digest = Fold(digest, line);
            digest = Fold(digest, (byte)'\n');
        }

        return digest;
    }

    private static ulong Fold(ulong h, string s)
    {
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            h = (h ^ b) * FnvPrime;
        }

        return h;
    }

    private static ulong Fold(ulong h, long v)
    {
        for (int i = 0; i < 8; i++)
        {
            h = (h ^ (byte)(v >> (8 * i))) * FnvPrime;
        }

        return h;
    }

    private static ulong Fold(ulong h, ulong v) => Fold(h, (long)v);

    private static ulong Fold(ulong h, byte b) => (h ^ b) * FnvPrime;
}
