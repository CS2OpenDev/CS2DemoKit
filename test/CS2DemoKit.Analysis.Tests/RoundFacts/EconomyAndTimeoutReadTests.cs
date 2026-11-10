#region

using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     The economy and timeout reads on a tournament demo whose numbers were checked by hand: legacy-vs-fut
///     m2 on dust2 (BLAST Open Porto 2026). No helmet on either pistol round; the CT side takes two
///     timeouts in the first half, so its count goes 3 to 2 to 1, and the counts swap at the half.
/// </summary>
[Category("Integration")]
[NotInParallel]
public class EconomyAndTimeoutReadTests
{
    internal const string PortoDust2 = "2026-08-27_blast-open-porto-2026_legacy-vs-fut_bo3-m2_dust2.dem";

    private const string Rules = """
        ruleset: tv
        for: match
        stats:
          start_side:
            capture: event.Side
            on: timeout_started
            keep: list
            per: match
          start_left:
            capture: event.TimeoutsLeft
            on: timeout_started
            keep: list
            per: match
          end_side:
            capture: event.Side
            on: timeout_ended
            keep: list
            per: match
          ct_left:
            capture: match.ct_timeouts_left
            on: round_decided
            keep: list
            per: match
          t_left:
            capture: match.t_timeouts_left
            on: round_decided
            keep: list
            per: match
        """;

    [Test]
    public async Task PortoDust2_PistolHelmets_TimeoutCounts_AndEvents()
    {
        string path = DemoTestHelper.RequireDemo(PortoDust2);
        IReadOnlyList<RulesetDoc> rules = RoundFactsTestSupport.Load(Rules);
        FreezeEndHelmets helmets = new();

        AnalysisRun run = DemoAnalysis.Run(path, rules, new AnalysisOptions
        {
            ObservedProviders = ["entity.pawn.has_helmet", "entity.controller.cash_spent",
                "entity.controller.start_money", "entity.controller.money"],
            ObservedGameProviders = ["entity.game.ct_timeout_active", "entity.game.t_timeout_active"],
            FrameObserver = helmets
        });

        await Assert.That(helmets.PerRound.Count).IsEqualTo(24);
        await Assert.That(helmets.PerRound[0]).IsEqualTo(0);
        await Assert.That(helmets.PerRound[12]).IsEqualTo(0);
        await Assert.That(helmets.EconomyMismatches).IsEqualTo(0)
            .Because("money at freeze end is start_money - cash_spent");

        string ct = string.Join(",", RoundFactsCorpusTests.Ints(run, "tv.ct_left"));
        string t = string.Join(",", RoundFactsCorpusTests.Ints(run, "tv.t_left"));
        await Assert.That(ct).IsEqualTo("3,3,3,3,2,2,2,1,1,1,1,1,3,3,3,3,3,3,3,2,1,1,1,1");
        await Assert.That(t).IsEqualTo("3,3,3,3,3,3,3,3,3,3,3,3,1,1,1,1,1,1,1,1,1,1,1,0");
        await Assert.That(string.Join(",", RoundFactsCorpusTests.Ints(run, "tv.start_side"))).IsEqualTo("3,3,3,3,2");
        await Assert.That(string.Join(",", RoundFactsCorpusTests.Ints(run, "tv.start_left"))).IsEqualTo("2,1,2,1,0");
        await Assert.That(string.Join(",", RoundFactsCorpusTests.Ints(run, "tv.end_side"))).IsEqualTo("3,3,3,3,2");
        await Assert.That(helmets.FlagRises).IsEqualTo(5).Because("the observer's game reads see every start");
    }

    private sealed class FreezeEndHelmets : IFrameObserver
    {
        public readonly List<int> PerRound = [];
        public int EconomyMismatches;
        public int FlagRises;
        private int _lastFlags = -1;

        public void OnFrame(FrameView frame)
        {
            frame.TryReadGameInt("entity.game.ct_timeout_active", out int ct);
            frame.TryReadGameInt("entity.game.t_timeout_active", out int t);
            int flags = ct + (t << 1);
            if (_lastFlags >= 0)
            {
                FlagRises += (flags & ~_lastFlags & 1) + ((flags & ~_lastFlags) >> 1);
            }

            _lastFlags = flags;
            if (!frame.Frame.DecodedMessages.Any(m => m is GameEventMessage { DecodedEvent.Name: "round_freeze_end" }))
            {
                return;
            }

            int count = 0;
            for (int slot = 0; slot < 64; slot++)
            {
                if (frame.Team(slot) is not (2 or 3))
                {
                    continue;
                }

                if (frame.Read("entity.pawn.has_helmet", slot) is true)
                {
                    count++;
                }

                if (frame.TryReadInt("entity.controller.cash_spent", slot, out int spent)
                    && frame.TryReadInt("entity.controller.start_money", slot, out int start)
                    && frame.TryReadInt("entity.controller.money", slot, out int money) && start - spent != money)
                {
                    EconomyMismatches++;
                }
            }

            PerRound.Add(count);
        }

        public void Finish() { }
    }
}
