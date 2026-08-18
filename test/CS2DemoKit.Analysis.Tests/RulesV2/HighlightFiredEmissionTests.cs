#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     A1 (rich highlight emission) end-to-end golden on the reference demo: a BARE-mode
///     (<c>CaptureSnapshots = false</c>) evaluation of the shipped highlight-bearing rules (kast +
///     post_plant_double) must yield fully-populated <see cref="HighlightFired" /> records —
///     qualified ids, frame-clock ticks inside the demo's range, RAW player names, rendered titles
///     with no unresolved <c>{…}</c> holes — and the emission-time <c>RoundNumber</c> must agree
///     with the snapshot-mode <see cref="RuleChainEventProjector" />'s round attribution for the
///     same events (the pipeline's whole premise: bare mode loses nothing the cache needs).
///     <para>
///         Parses the demo, so <see cref="NotInParallelAttribute" /> and the shared parse cache
///         apply (ONE heavy demo parse machine-wide); the snapshot comparison run is sequential in
///         the same test. Two separate builds are used — game-scoped nodes are shared mutable
///         state, so re-evaluating one <see cref="BuildResult" /> would double-count rounds.
///     </para>
/// </summary>
[Category("Integration")]
[NotInParallel]
public class HighlightFiredEmissionTests
{
    private static readonly HashSet<(string Ruleset, string Highlight)> _shippedHighlights =
        [("kast", "kast"), ("post_plant_double", "post_plant_double")];

    [Test]
    public async Task BareMode_EmitsRichRecords_AndRoundsMatchSnapshotProjector()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        IReadOnlyList<RulesetDoc> docs = LoadShippedDocs();

        // ── Bare-mode run (the Highlights pipeline's scan mode) ──
        BuildResult bareBuild = DemoAnalysis.Build(demo, docs);
        AnalysisRun bare = DemoAnalysis.Evaluate(demo, bareBuild,
            new AnalysisOptions
            {
                CaptureSnapshots = false
            });

        await Assert.That(bare.Snapshots).IsNull()
            .Because("this must be a genuinely bare run — the emission may not depend on snapshots");
        await Assert.That(bare.Highlights.Count).IsGreaterThan(0)
            .Because("the reference demo fires both shipped highlights (KAST rounds + post-plant doubles)");

        int lastTick = demo.Frames[^1].ServerTick;
        foreach (HighlightFired hf in bare.Highlights)
        {
            await Assert.That(_shippedHighlights.Contains((hf.RulesetId, hf.HighlightId))).IsTrue()
                .Because($"({hf.RulesetId}, {hf.HighlightId}) must be a declared shipped highlight — "
                         + "the qualified identity fixes the timeline's lost-qualifier problem");
            await Assert.That(hf.Tick).IsGreaterThan(0)
                .Because("highlights fire on gameplay frames (frame clock runs 1, 2, …)");
            await Assert.That(hf.Tick).IsLessThanOrEqualTo(lastTick)
                .Because("the firing tick must be inside the demo's frame-clock range");
            await Assert.That(hf.FrameIndex).IsGreaterThanOrEqualTo(0);
            await Assert.That(hf.FrameIndex).IsLessThan(demo.Frames.Count);
            await Assert.That(string.IsNullOrWhiteSpace(hf.PlayerName)).IsFalse()
                .Because("every lowered highlight is per-player; the RAW in-demo name must be present");
            await Assert.That(hf.RoundNumber).IsGreaterThanOrEqualTo(1)
                .Because("both shipped highlights only fire during live rounds");
            await Assert.That(hf.RenderedTitle.Contains('{')).IsFalse()
                .Because($"the shipped titles must render with no unresolved holes, got '{hf.RenderedTitle}'");
            await Assert.That(hf.RenderedTitle).Contains(hf.PlayerName)
                .Because("both shipped titles carry a {player.name} hole");
            if (hf.RulesetId == "post_plant_double")
            {
                string roundText = hf.RoundNumber.ToString(CultureInfo.InvariantCulture);
                await Assert.That(hf.RenderedTitle).Contains($"(round {roundText})")
                    .Because("the {round.number} hole must render the same round the record carries");
            }
        }

        // ── Emission ↔ timeline agreement (same rising edges, same stamps) ──
        foreach ((string _, string highlightId) in _shippedHighlights)
        {
            string chainName = $"_chain_{highlightId}";
            List<(int Frame, int Tick, int? Slot)> timelineStamps = bare.Timeline.Events
                .Where(e => e.ChainName == chainName)
                .Select(e => (Frame: e.FrameIndex, e.Tick, Slot: e.PlayerSlot))
                .OrderBy(t => t.Frame).ThenBy(t => t.Slot).ToList();
            List<(int Frame, int Tick, int? Slot)> emittedStamps = bare.Highlights
                .Where(h => h.HighlightId == highlightId)
                .Select(h => (Frame: h.FrameIndex, h.Tick, Slot: (int?)h.PlayerSlot))
                .OrderBy(t => t.Frame).ThenBy(t => t.Slot).ToList();
            await Assert.That(emittedStamps).IsEquivalentTo(timelineStamps)
                .Because($"every '{chainName}' timeline rising edge must have exactly one "
                         + "HighlightFired with the identical (frame, tick, slot) stamp");
        }

        // ── Snapshot-mode comparison run (fresh build — see class doc) ──
        BuildResult snapBuild = DemoAnalysis.Build(demo, docs);
        AnalysisRun snap = DemoAnalysis.Evaluate(demo, snapBuild, new AnalysisOptions());

        await Assert.That(snap.Highlights).IsEquivalentTo(bare.Highlights)
            .Because("emission must be mode-independent — snapshot capture changes nothing "
                     + "about what fires or what is rendered");

        // ── RoundNumber parity vs the snapshot projector's round attribution ──
        MetricTable events = new RuleChainEventProjector().Project(snap.Snapshots!, demo)
            .Single(t => t.Name == RuleChainEventProjector.TableName);
        List<(string Chain, int Slot, int Frame, int Round)> projected = events.Rows
            .Where(r => r.Dimensions.GetValueOrDefault("chain") is string c
                        && _shippedHighlights.Any(s => s.Highlight == c))
            .Select(r => (
                (string)r.Dimensions["chain"]!,
                Convert.ToInt32(r.Dimensions["player_slot"], CultureInfo.InvariantCulture),
                Convert.ToInt32(r.Dimensions["frame_index"], CultureInfo.InvariantCulture),
                Convert.ToInt32(r.Dimensions["round_number"], CultureInfo.InvariantCulture)))
            .ToList();

        await Assert.That(projected.Count).IsEqualTo(snap.Highlights.Count)
            .Because("the projector and the emission must describe the same event set");

        foreach (HighlightFired hf in snap.Highlights)
        {
            (string Chain, int Slot, int Frame, int Round) match = projected
                .Single(p => p.Chain == hf.HighlightId && p.Slot == hf.PlayerSlot && p.Frame == hf.FrameIndex);
            await Assert.That(hf.RoundNumber).IsEqualTo(match.Round)
                .Because($"emission-time round attribution for {hf.HighlightId}@frame {hf.FrameIndex} "
                         + "(live round_number node read) must match the snapshot projector's "
                         + "round-by-frame replay");
        }
    }

    private static IReadOnlyList<RulesetDoc> LoadShippedDocs()
    {
        string rulesDir = Path.Combine(FindRepoRoot(), "src", "CS2DemoKit.Analysis", "Rules");
        return
        [
            LoadFile(Path.Combine(rulesDir, "kast.rules.yaml")),
            LoadFile(Path.Combine(rulesDir, "post_plant_double.rules.yaml"))
        ];

        static RulesetDoc LoadFile(string path)
        {
            return RulesetDocumentLoader.Load(File.ReadAllText(path), Path.GetFileName(path)).Doc
                   ?? throw new InvalidOperationException($"shipped ruleset failed to load: {path}");
        }
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CS2DemoKit.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("repo root not found");
    }
}
