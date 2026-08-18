#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;

#endregion

using CS2DemoKit.TestSupport;

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Two latent lazy-materialization
///     bugs.
///     <list type="number">
///         <item>
///             <b>Missing 0..63 sentinel guard</b> — <c>ExtractPlayerSlots</c> guarded
///             Killer/Assister/Attacker slots but yielded <c>UserId</c>/<c>PlayerSlot</c>
///             unguarded (game-event slot keys pass through the decoder's ValShort with no
///             clamp), so a -1/65535 slot key materialized a phantom "Player -1" into
///             <c>MaterializedPlayers</c> and every per-player stats projection. The guard is
///             now hoisted to the single consumption site in <c>MaterializeNewPlayers</c>.
///         </item>
///         <item>
///             <b>Synthesized events never materialized</b> — the synthesized-entity-event loop
///             (molotov_thrown) never called <c>MaterializeNewPlayers</c> and the extraction
///             switch had no <c>MolotovThrownEvent</c> case, so a player whose first qualifying
///             activity is entity-derived (mid-match-start demos) silently lost those events.
///         </item>
///     </list>
///     Pure in-memory — synthetic frames and injected digests, no demo file.
/// </summary>
[Category("Unit")]
public class MaterializationGuardTests
{
    /// <summary>A per-player template that records materialized slots and builds nothing.</summary>
    private static (StateGraph Graph, List<int> Materialized) RecordingGraph()
    {
        List<int> materialized = [];
        StateGraph graph = new();
        graph.AddPerPlayerTemplate(new PerPlayerNodeTemplate((slot, _, name, _) =>
        {
            materialized.Add(slot);
            return new PerPlayerNodeTemplate.MaterializedPlayer(slot, name, [], [], [], []);
        }));
        return (graph, materialized);
    }

    private static DemoFrame Frame(params NetMessage[] msgs) => new()
    {
        Command = "DEM_Packet",
        FrameNumber = 0,
        ServerTick = 0,
        RawStart = 0,
        RawLength = 1,
        HeaderLength = 1,
        IsCompressed = false,
        MessageList = [.. msgs]
    };

    private static GameEventMessage TeamEvent(int slot) => GameEventMessage.ForSynthesizedEvent(
        TestGameEvents.PlayerTeam(slot, 2));

    private static GameEventMessage DeathEvent(int victimSlot, int killerSlot, int assisterSlot) =>
        GameEventMessage.ForSynthesizedEvent(TestGameEvents.PlayerDeath(victimSlot, killerSlot, assisterSlot, "ak47"));

    /// <summary>
    ///     Sentinel slots (-1 no-player, 65535 wire garbage) must materialize nothing — the
    ///     previously-unguarded UserId/PlayerSlot yields are the regression pins. FAILS
    ///     pre-fix (materialized contains -1 and 65535).
    /// </summary>
    [Test]
    public async Task SentinelSlot_MaterializesNothing()
    {
        (StateGraph graph, List<int> materialized) = RecordingGraph();

        DemoFrame frame = Frame(
            TeamEvent(-1),
            TeamEvent(65535),
            DeathEvent(-1, -1, -1));

        new StateGraphEvaluator(graph).Evaluate([frame]);

        await Assert.That(materialized).IsEmpty()
            .Because("sentinel slots must never materialize phantom players");
    }

    /// <summary>Control for the sentinel test: a valid slot still materializes exactly once.</summary>
    [Test]
    public async Task ValidSlot_Materializes()
    {
        (StateGraph graph, List<int> materialized) = RecordingGraph();

        new StateGraphEvaluator(graph).Evaluate([Frame(TeamEvent(5), TeamEvent(5))]);

        await Assert.That(materialized).IsEquivalentTo([5])
            .Because("a real slot materializes once; the seen-set dedups the second event");
    }

    /// <summary>
    ///     The synthesized-event loop now materializes players: a molotov_thrown synthesized
    ///     from an injected entity digest materializes its thrower. FAILS pre-fix (the loop
    ///     never materialized AND the switch had no MolotovThrownEvent case). demo: null keeps
    ///     the constructor's known-roster path inert, isolating the loop path.
    /// </summary>
    [Test]
    public async Task SynthesizedEventLoop_MaterializesThrower()
    {
        (StateGraph graph, List<int> materialized) = RecordingGraph();

        EntityChangeScanner scanner = new(new EntityStateLayer([]), [], null, true);
        EntityFrameDigest digest = new();
        digest.Molotovs.Add((Index: 1, Serial: 1, ThrowerSlot: 7));
        scanner.SetPrecomputedDigests([digest]);

        new StateGraphEvaluator(graph, null, null, scanner).Evaluate([Frame()]);

        await Assert.That(materialized).IsEquivalentTo([7])
            .Because("a player whose first activity is a synthesized event must materialize");
    }

    /// <summary>
    ///     The hoisted guard also covers the synthesized path: an out-of-range-high thrower
    ///     slot materializes nothing. (Negative throwers are already dropped at the scanner —
    ///     ConsumeMolotovs skips slot &lt; 0 — so ≥ 64 is the value that exercises the guard.)
    /// </summary>
    [Test]
    public async Task SynthesizedEventLoop_SentinelSlot_MaterializesNothing()
    {
        (StateGraph graph, List<int> materialized) = RecordingGraph();

        EntityChangeScanner scanner = new(new EntityStateLayer([]), [], null, true);
        EntityFrameDigest digest = new();
        digest.Molotovs.Add((Index: 1, Serial: 1, ThrowerSlot: 64));
        scanner.SetPrecomputedDigests([digest]);

        new StateGraphEvaluator(graph, null, null, scanner).Evaluate([Frame()]);

        await Assert.That(materialized).IsEmpty()
            .Because("the 0..63 guard must cover the synthesized-event path too");
    }
}
