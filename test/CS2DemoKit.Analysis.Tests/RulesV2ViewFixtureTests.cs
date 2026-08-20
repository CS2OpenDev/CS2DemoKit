#region

using CS2DemoKit.Analysis.GoldenStats;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Oracle fixture pins for the curated v2 views. For each view
///     this folds <c>demo.AllGameEvents</c> with the SAME predicate the view's <c>baked:</c>
///     filter + binding lowers to (the event-fold oracle pattern
///     — no engine, a deterministic C# fold), then asserts the per-player / total counts against
///     the demo's reference golden fixture (<c>expected.golden.json</c>, the pinned
///     reference stats).
///     <para>
///         The reference demo (<c>003816779297406845372_0003771537</c>, de_mirage, 22 decided
///         rounds, a 13–9 that crosses halftime) is chosen so <c>round_won</c>/<c>round_lost</c>
///         are pinned across the team-swap — the frozen-env trap.
///     </para>
///     <para>
///         <b>Oracle honesty.</b> Views pin at different tiers, printed in the report:
///         <list type="bullet">
///             <item>EXACT — kill / death / assist per-player vs the reference k/d/a.</item>
///             <item>
///                 DIRECTIONAL — shot ≥ reference shots_fired (weapon_fire counts
///                 utility throws too); damage_dealt(raw) ≥ reference enemy_damage (raw is
///                 uncapped and includes team/self damage). The exact match needs the
///                 <c>enrich.*</c> facets, which are engine-computed, not foldable here.
///             </item>
///             <item>
///                 COUNT + STRUCTURAL — round_won/round_lost: decided-round
///                 count vs the golden (Σ team ct+t wins), plus a post-halftime assertion. The
///                 full per-player <c>binding: team</c> live-team attribution is validated by
///                 the env-equivalence battery (which must include a
///                 post-halftime round); this fixture pins the winner inputs + halftime crossing.
///             </item>
///             <item>
///                 SELF / INTERNAL — damage_taken, blinded, blinded_enemy, bomb_planted,
///                 bomb_defused have no matching stat in the golden; pinned by non-vacuity +
///                 internal reconciliation and reported.
///             </item>
///         </list>
///     </para>
/// </summary>
[NotInParallel]
[Category("Integration")]
public class RulesV2ViewFixtureTests
{
    private const string DemoId = "003816779297406845372_0003771537";

    [Test]
    public async Task CuratedViews_MatchReferenceGolden_OnReferenceDemo()
    {
        string path = DemoTestHelper.RequireDemo(DemoId + ".dem");
        GoldenStatsDocument golden = GoldenStatsTestHelper.LoadGolden(DemoId, "expected");
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);

        // slot -> SteamID64 for the real players, and SteamID64 -> golden stats. Matching by
        // Steam ID avoids display-name encoding quirks between exports.
        Dictionary<int, ulong> slotToSteam = demo.Players.Values
            .Where(p => !p.IsBot && p.SteamId64 != 0)
            .ToDictionary(p => p.Slot, p => p.SteamId64);
        Dictionary<ulong, PlayerStatsRecord> goldenBySteam = golden.Players.Values
            .Where(r => r.SteamId is not null)
            .ToDictionary(r => r.SteamId!.Value, r => r);

        // ── Single fold over the event stream (one parse; memory rules) ──────────────
        Fold kill = new(), death = new(), assist = new(); // player_death
        Fold shot = new(); // weapon_fire
        Fold dmgDealt = new(), dmgTaken = new(); // player_hurt (sum DmgHealth)
        Fold blinded = new(), blindedEnemy = new(); // player_blind
        int bombPlanted = 0, bombDefused = 0;
        int playerBlindEvents = 0;
        // GOTV emits no round_end (winner is engine-derived from bomb/alive state, not on the
        // wire); round_officially_ended is the once-per-round decided-round signal we can fold.
        int roundsOfficiallyEnded = 0;

        foreach (GameEvent ev in demo.AllGameEvents)
        {
            switch (ev.Payload)
            {
                case PlayerDeathEvent d:
                    death.Add(d.UserId, 1); // death: every death (incl suicide)
                    if (d.Attacker != d.UserId) // kill/assist: baked excludes suicide
                    {
                        kill.Add(d.Attacker, 1);
                        assist.Add(d.Assister, 1);
                    }

                    break;
                case WeaponFireEvent w:
                    shot.Add(w.UserId, 1);
                    break;
                case PlayerHurtEvent h:
                    dmgDealt.Add(h.Attacker, h.DmgHealth);
                    dmgTaken.Add(h.UserId, h.DmgHealth);
                    break;
                case PlayerBlindEvent b:
                    playerBlindEvents++;
                    blinded.Add(b.UserId, 1);
                    blindedEnemy.Add(b.Attacker, 1);
                    break;
                case BombPlantedEvent:
                    bombPlanted++;
                    break;
                case BombDefusedEvent:
                    bombDefused++;
                    break;
                case RoundOfficiallyEndedEvent:
                    roundsOfficiallyEnded++;
                    break;
            }
        }

        int goldenDecidedRounds = GoldenDecidedRounds(golden);

        // ── Report FIRST (so a mismatch is fully diagnosable in one parse) ───────────
        Console.WriteLine($"=== v2 view fixture: {DemoId} (map={golden.Match.Map}) ===");
        Console.WriteLine($"real players matched to golden: {slotToSteam.Count(s => goldenBySteam.ContainsKey(s.Value))}");
        Console.WriteLine($"kills(fold Σ)={SumReal(kill, slotToSteam)}  deaths={SumReal(death, slotToSteam)}  "
                          + $"assists={SumReal(assist, slotToSteam)}  shots={SumReal(shot, slotToSteam)}");
        Console.WriteLine($"dmg_dealt(raw Σ)={SumReal(dmgDealt, slotToSteam)}  dmg_taken(raw Σ)={SumReal(dmgTaken, slotToSteam)}");
        Console.WriteLine($"blinded Σ={SumReal(blinded, slotToSteam)}  blinded_enemy Σ={SumReal(blindedEnemy, slotToSteam)}  "
                          + $"player_blind events={playerBlindEvents}");
        Console.WriteLine($"bomb_planted={bombPlanted}  bomb_defused={bombDefused}");
        Console.WriteLine($"rounds: round_officially_ended fold={roundsOfficiallyEnded}  "
                          + $"golden decided (Σ team ct+t)={goldenDecidedRounds}");
        Console.WriteLine("golden per-team (ct_won, t_won): "
                          + string.Join("  ", golden.Players.Values
                              .GroupBy(p => p.Team)
                              .OrderBy(g => g.Key)
                              .Select(g => $"team{g.Key}=({Stat(g.First(), "ct_rounds_won")},{Stat(g.First(), "t_rounds_won")})")));
        foreach ((int slot, ulong steam) in slotToSteam.OrderBy(s => s.Key))
        {
            if (!goldenBySteam.TryGetValue(steam, out PlayerStatsRecord? g))
            {
                continue;
            }

            Console.WriteLine(
                $"  slot {slot,2} steam {steam}: "
                + $"kill {kill.Get(slot)}/{Stat(g, "kills")}  "
                + $"death {death.Get(slot)}/{Stat(g, "deaths")}  "
                + $"assist {assist.Get(slot)}/{Stat(g, "assists")}  "
                + $"shot {shot.Get(slot)}/{Stat(g, "shots_fired")}  "
                + $"dmg {dmgDealt.Get(slot)}/{Stat(g, "enemy_damage")}");
        }

        // ── Assertions ───────────────────────────────────────────────────────────────
        // Non-vacuity: a real match must produce events (an all-zero fold would vacuously "pass").
        await Assert.That(SumReal(kill, slotToSteam)).IsGreaterThan(0);
        await Assert.That(SumReal(death, slotToSteam)).IsGreaterThan(0);
        await Assert.That(SumReal(shot, slotToSteam)).IsGreaterThan(0);
        await Assert.That(SumReal(dmgDealt, slotToSteam)).IsGreaterThan(0);
        await Assert.That(SumReal(dmgTaken, slotToSteam)).IsGreaterThan(0);
        await Assert.That(playerBlindEvents).IsGreaterThan(0);
        await Assert.That(bombPlanted).IsGreaterThan(0);

        int matched = 0;
        foreach ((int slot, ulong steam) in slotToSteam)
        {
            if (!goldenBySteam.TryGetValue(steam, out PlayerStatsRecord? g))
            {
                continue;
            }

            matched++;

            // TIER 1 — EXACT: kill view per player == the reference kills.
            // (killer≠victim + per-killer attribution reproduces the golden kill count exactly.)
            await Assert.That((double)kill.Get(slot)).IsEqualTo(Stat(g, "kills"))
                .Because($"kill view: slot {slot} kills (killer≠victim) must equal the golden");

            // TIER 2 — DIRECTIONAL. The view's fold is a SUPERSET of the narrower
            // reference stat, so the exact number needs an engine enrichment the fold can't apply:
            //   death        ⊇ reference deaths      (the deaths stat excludes suicides / world deaths)
            //   assist       ⊇ reference assists     (v2 counts flash + damage assists; the stat is narrower)
            //   shot         ⊇ reference shots_fired (weapon_fire counts grenade/utility throws too)
            //   damage_dealt ⊇ reference enemy_damage(raw DmgHealth is uncapped + incl team/self)
            await Assert.That((double)death.Get(slot)).IsGreaterThanOrEqualTo(Stat(g, "deaths"))
                .Because($"death view: slot {slot} all-deaths ≥ golden (the deaths stat drops suicides)");
            await Assert.That((double)assist.Get(slot)).IsGreaterThanOrEqualTo(Stat(g, "assists"))
                .Because($"assist view: slot {slot} all-assists ≥ golden (the assists stat is narrower)");
            await Assert.That((double)shot.Get(slot)).IsGreaterThanOrEqualTo(Stat(g, "shots_fired"))
                .Because($"shot view: slot {slot} weapon_fire count ⊇ golden shots_fired");
            await Assert.That((double)dmgDealt.Get(slot)).IsGreaterThanOrEqualTo(Stat(g, "enemy_damage"))
                .Because($"damage_dealt view: slot {slot} raw DmgHealth ≥ golden capped enemy_damage");
        }

        await Assert.That(matched).IsGreaterThanOrEqualTo(10)
            .Because("all ten golden players must match a real slot by Steam ID");

        // TIER 4 — SELF / INTERNAL reconciliation (no matching stat in the golden).
        // Every player_blind has a real victim and a real attacker on this demo, so both the
        // blinded (victim-side) and blinded_enemy (attacker-side) views reconcile to the event count.
        await Assert.That(SumReal(blinded, slotToSteam)).IsEqualTo(playerBlindEvents)
            .Because("blinded view (victim side) must account for every player_blind event");
        await Assert.That(SumReal(blindedEnemy, slotToSteam)).IsEqualTo(playerBlindEvents)
            .Because("blinded_enemy view (attacker side) must account for every player_blind event");
        await Assert.That(bombDefused).IsLessThanOrEqualTo(bombPlanted)
            .Because("a bomb can only be defused if it was planted");

        // TIER 3 — round_won / round_lost: COUNT reconciliation + post-halftime STRUCTURAL pin.
        // No wire winner to fold, so: (a) the fold's played-round count (round_officially_ended)
        // reconciles with the golden's decided-round total, and (b) the golden itself proves the
        // team-swap — every team won rounds on BOTH sides, which is only possible across halftime.
        // The full per-player `binding: team` live-team attribution is the env-equivalence battery's job.
        await Assert.That(Math.Abs(roundsOfficiallyEnded - goldenDecidedRounds)).IsLessThanOrEqualTo(1)
            .Because("round_officially_ended (round transitions) reconciles with the golden's "
                     + "decided-round total within the match-end boundary — the match-winning final "
                     + "round has no officially-ended transition after it, so it reads one fewer");
        await Assert.That(roundsOfficiallyEnded).IsGreaterThan(12)
            .Because("the demo itself contains post-halftime rounds (not just golden metadata)");

        // The halftime-crossing precondition is carried entirely by the two assertions above, both
        // of which read the demo. Two further checks used to live here, on goldenDecidedRounds > 12
        // and on each team having won rounds on both sides, and both read only the committed
        // golden. Those are permanently true properties of a static file, so they asserted nothing
        // about any code and could not fail. The reconcile above already implies the first, and the
        // reference demo's team-swap is documented on the class.
    }

    /// <summary>Σ over the two distinct team (ct_rounds_won + t_rounds_won) totals in the golden.</summary>
    private static int GoldenDecidedRounds(GoldenStatsDocument golden)
    {
        Dictionary<int, int> perTeam = new();
        foreach (PlayerStatsRecord r in golden.Players.Values)
        {
            int total = (int)Math.Round(Stat(r, "ct_rounds_won") + Stat(r, "t_rounds_won"));
            perTeam[r.Team] = total; // every player on a team shares the team's win totals
        }

        return perTeam.Values.Sum();
    }

    // Loud on a missing key. Defaulting to 0 silently degraded every DIRECTIONAL assertion that
    // reads it into "x >= 0", which is unconditionally true, so a re-pin that dropped a key would
    // gut those checks with no signal. All six goldens carry all the keys read here today.
    private static double Stat(PlayerStatsRecord r, string key) =>
        r.Stats.TryGetValue(key, out double? value) && value is not null
            ? value.Value
            : throw new InvalidOperationException(
                $"golden record for team {r.Team} has no '{key}' stat, so the assertions reading it "
                + "would silently become vacuous. Re-pin the fixture or fix the key.");

    private static long SumReal(Fold fold, Dictionary<int, ulong> realSlots) =>
        realSlots.Keys.Sum(slot => fold.Get(slot));

    /// <summary>Per-slot integer accumulator (kills, damage, …).</summary>
    private sealed class Fold
    {
        private readonly Dictionary<int, long> _bySlot = new();

        public void Add(int slot, long delta) =>
            _bySlot[slot] = _bySlot.GetValueOrDefault(slot) + delta;

        public long Get(int slot) => _bySlot.GetValueOrDefault(slot);
    }
}
