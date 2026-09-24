#region

using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Output;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Parser;
using CS2DemoKit.TestSupport;

#endregion

namespace CS2DemoKit.Analysis.Tests.RoundFacts;

/// <summary>
///     <c>for: each_team</c> (issue #54, piece 1): one instance per side, rows keyed by (round, side),
///     views bound to the side, <c>round.team.*</c> relative to it, and the side's roster as the
///     table's <c>slots</c> dimension.
/// </summary>
[Category("Unit")]
public class TeamScopeTests
{
    private const string PerSideRound = """
        ruleset: sides
        for: each_team
        stats:
          kills:
            count: kill
            per: round
          won:
            count: round_won
            per: round
          lost:
            count: round_lost
            per: round
          equipment:
            capture: round.team.equipment
            on: raw.round_freeze_end
            per: round
          enemy_equipment:
            capture: round.enemies.equipment
            on: raw.round_freeze_end
            per: round
          money:
            capture: round.team.money
            on: raw.round_freeze_end
            per: round
          enemy_money:
            capture: round.enemies.money
            on: raw.round_freeze_end
            per: round
          players:
            capture: round.team.players
            on: raw.round_freeze_end
            per: round
          enemy_players:
            capture: round.enemies.players
            on: raw.round_freeze_end
            per: round
          alive:
            capture: round.team.alive
            on: raw.round_freeze_end
            per: round
          enemies_alive:
            capture: round.enemies.alive
            on: raw.round_freeze_end
            per: round
          alive_after_kill:
            capture: round.team.alive
            on: kill
            match: { actor: any }
            keep: list
            per: round
          enemies_alive_after_kill:
            capture: round.enemies.alive
            on: kill
            match: { actor: any }
            keep: list
            per: round
          side:
            capture: team.side
            on: round_ended
            per: round
          match_kills:
            count: kill
            per: match
        show:
          tables:
            sides:
              per: team_round
              columns:
                - { stat: kills, label: K }
                - { stat: won, label: W }
                - { stat: lost, label: L }
                - { stat: equipment, label: EQ }
                - { stat: enemy_equipment, label: EEQ }
                - { stat: money, label: M }
                - { stat: enemy_money, label: EM }
                - { stat: players, label: TP }
                - { stat: enemy_players, label: EP }
                - { stat: alive, label: TA }
                - { stat: enemies_alive, label: EA }
                - { stat: alive_after_kill, label: TAK }
                - { stat: enemies_alive_after_kill, label: EAK }
                - { stat: side, label: S }
            sides_match:
              per: team_match
              columns:
                - { stat: match_kills, label: K }
        """;

    private const string PerPlayerRound = """
        ruleset: players
        for: each_player
        stats:
          kills:
            count: kill
            per: round
          equipment:
            capture: round.team.equipment
            on: raw.round_freeze_end
            per: round
          money:
            capture: player.money
            on: raw.round_freeze_end
            per: round
        show:
          tables:
            players:
              per: player_round
              columns:
                - { stat: kills, label: K }
                - { stat: equipment, label: EQ }
                - { stat: money, label: M }
        """;

    // ── resolver and validator ─────────────────────────────────────────────

    [Test]
    public async Task EachTeam_Parses_AndResolvesTeamSide_OnTeamAxes()
    {
        CheckedRuleset rs = RoundFactsTestSupport.Checked(PerSideRound);
        await Assert.That(rs.For).IsEqualTo(RulesetScope.EachTeam);
        await Assert.That(rs.Stats.Single(s => s.StatId == "kills").Scope).IsEqualTo(ScopeAxis.TeamRound);
        await Assert.That(rs.Stats.Single(s => s.StatId == "match_kills").Scope).IsEqualTo(ScopeAxis.TeamMatch);
        await Assert.That(rs.Stats.Single(s => s.StatId == "side").DeclaredReads).Contains("team.side");

        // The team views declare the winner reads their side binding makes.
        await Assert.That(rs.Stats.Single(s => s.StatId == "won").DeclaredReads).Contains("enrich.round.winner_team");
    }

    [Test]
    public async Task TeamAxes_HashApartFromTheOtherScopes()
    {
        CheckedStat team = RoundFactsTestSupport.Checked(PerSideRound).Stats.Single(s => s.StatId == "kills");
        CheckedStat match = RoundFactsTestSupport.Checked("""
            ruleset: sides
            for: match
            stats:
              kills:
                count: kill
                per: round
            """).Stats.Single();

        MapStatHashSource none = new(new Dictionary<string, ReadOnlyMemory<byte>>());
        await Assert.That(Convert.ToHexString(V2StatHasher.Hash(team, none)))
            .IsNotEqualTo(Convert.ToHexString(V2StatHasher.Hash(match, none)));
    }

    [Test]
    public async Task EachTeam_RejectsPlayerReads()
    {
        RulesetResolveResult result = RoundFactsTestSupport.Resolve("""
            ruleset: bad
            for: each_team
            stats:
              s:
                count: kill
                where: player.alive
                per: round
            """);
        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.Diagnostics.Any(d => d.Code == DiagnosticCodes.UnknownRoot)).IsTrue();
    }

    [Test]
    [Arguments("round.alive.in_clutch")]
    [Arguments("round.clutch.size > 0")]
    public async Task EachTeam_RejectsTheClutchReads_WhoseSubjectIsAPlayer(string where)
    {
        RulesetResolveResult result = RoundFactsTestSupport.Resolve($$"""
            ruleset: bad
            for: each_team
            stats:
              s:
                count: kill
                where: "{{where}}"
                per: round
            """);
        await Assert.That(result.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TeamScopeUnsupported)).IsTrue();
    }

    [Test]
    public async Task EachTeam_RejectsHighlights_AndScoreboards()
    {
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load("""
            ruleset: bad
            for: each_team
            stats:
              kills:
                count: kill
                per: round
            highlights:
              busy:
                when: "kills >= 3"
                title: "busy round"
            show:
              scoreboard:
                - { stat: kills, label: K }
            """));

        await Assert.That(validation.Success).IsFalse();
        await Assert.That(validation.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TeamScopeUnsupported)).IsTrue();
    }

    [Test]
    [Arguments("match", "player_round")]
    [Arguments("each_player", "team_round")]
    [Arguments("each_team", "player_match")]
    [Arguments("each_team", "match")]
    public async Task ATableOfTheWrongScope_IsAValidationError(string scope, string per)
    {
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load($$"""
            ruleset: mismatch
            for: {{scope}}
            stats:
              kills:
                count: kill
                per: round
            show:
              tables:
                t:
                  per: {{per}}
                  columns:
                    - { stat: kills, label: K }
            """));

        await Assert.That(validation.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.ShowTableScopeMismatch))
            .IsTrue().Because($"for: {scope} with per: {per}");
    }

    [Test]
    public async Task AScoreboard_OutsideEachPlayer_IsAValidationError()
    {
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load("""
            ruleset: sb
            for: match
            stats:
              kills:
                count: kill
                per: match
            show:
              scoreboard:
                - { stat: kills, label: K }
            """));

        await Assert.That(validation.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.ShowScoreboardScope)).IsTrue();
    }

    /// <summary>
    ///     A team ruleset may read a match ruleset and another team ruleset; nothing else may read a
    ///     team ruleset, and a team ruleset may not read a per-player one.
    /// </summary>
    [Test]
    [Arguments("each_team", "match", true)]
    [Arguments("each_team", "each_team", true)]
    [Arguments("each_team", "each_player", false)]
    [Arguments("each_player", "each_team", false)]
    [Arguments("match", "each_team", false)]
    public async Task CrossRulesetReads_FollowTheScopeMatrix(string reader, string readee, bool legal)
    {
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load($$"""
            ruleset: source
            for: {{readee}}
            exports: [kills]
            stats:
              kills:
                count: kill
                per: match
            """, $$"""
            ruleset: reader
            for: {{reader}}
            use: [source]
            stats:
              doubled:
                compute: "source.kills * 2"
                per: match
            """));

        bool scopeError = validation.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.CrossRefReadScope);
        await Assert.That(scopeError).IsEqualTo(!legal).Because($"{reader} reading {readee}");
    }

    /// <summary>
    ///     A team ruleset reads a used ruleset's stat in compute: only. In a where: or a capture:
    ///     the read is a validation error; both used to validate clean and throw at build.
    /// </summary>
    [Test]
    [Arguments("each_team", "where")]
    [Arguments("each_team", "capture")]
    [Arguments("match", "where")]
    [Arguments("match", "capture")]
    public async Task ATeamRuleset_ReadsAnotherRulesetsStat_InComputeOnly(string readee, string site)
    {
        string stat = site == "where"
            ? """
                s:
                  count: kill
                  where: "source.kills > 0"
                  per: round
              """
            : """
                s:
                  capture: source.kills
                  on: round_ended
                  per: round
              """;
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load($$"""
            ruleset: source
            for: {{readee}}
            exports: [kills]
            stats:
              kills:
                count: kill
                per: round
            """, """
            ruleset: reader
            for: each_team
            use: [source]
            stats:

            """ + stat));

        await Assert.That(validation.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TeamScopeUnsupported
                                                          && d.Message.Contains("source.kills", StringComparison.Ordinal)))
            .IsTrue().Because(string.Join("; ", validation.Diagnostics.Select(d => d.Code + " " + d.Message)));
    }

    [Test]
    public async Task ATeamRuleset_ReadsTheClutchSize_InATally_IsAValidationError()
    {
        RulesetValidationResult validation = DemoAnalysis.ValidateRulesets(RoundFactsTestSupport.Load("""
            ruleset: bad
            for: each_team
            stats:
              c1:
                count: kill
                per: round
              t:
                tally: round.clutch.size
                thresholds:
                  - { min: 1, target: c1 }
                per: round
            """));

        await Assert.That(validation.Diagnostics.Any(d => d.Code == ResolveDiagnosticCodes.TeamScopeUnsupported)).IsTrue();
    }

    /// <summary>
    ///     A team ruleset's compute: over a match stat and over another team ruleset's stat builds and
    ///     evaluates: every side reads the match's kills that round, and a side reads its own kills
    ///     through the other team ruleset.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_ATeamRuleset_ComputesOverMatchAndTeamStats()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, """
            ruleset: all_kills
            for: match
            exports: [kills]
            stats:
              kills:
                count: kill
                per: round
            """, """
            ruleset: side_kills
            for: each_team
            exports: [kills]
            stats:
              kills:
                count: kill
                per: round
            """, """
            ruleset: reader
            for: each_team
            use: [all_kills, side_kills]
            stats:
              round_kills:
                compute: "all_kills.kills"
                per: round
              doubled:
                compute: "side_kills.kills * 2"
                per: round
              own:
                count: kill
                per: round
            show:
              tables:
                r:
                  per: team_round
                  columns:
                    - { stat: round_kills, label: RK }
                    - { stat: doubled, label: D }
                    - { stat: own, label: K }
            """);

        MetricTable table = RoundFactsTestSupport.Table(run, "r", demo);
        await Assert.That(table.Rows.Count).IsGreaterThan(0);
        foreach (IGrouping<int, MetricRow> round in table.Rows.GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")))
        {
            int roundKills = round.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0);
            foreach (MetricRow side in round)
            {
                await Assert.That(RoundFactsTestSupport.Int(side, "RK")).IsEqualTo(roundKills);
                await Assert.That(RoundFactsTestSupport.Int(side, "D")).IsEqualTo(2 * (RoundFactsTestSupport.Int(side, "K") ?? 0));
            }
        }
    }

    [Test]
    public async Task TeamTables_LowerToTheTeamScopes()
    {
        IReadOnlyList<OutputDef> outputs = ShowLowering.LowerTables(RoundFactsTestSupport.Checked(PerSideRound));
        OutputDef round = outputs.Single(o => o.Id == "sides");
        OutputDef match = outputs.Single(o => o.Id == "sides_match");
        await Assert.That(round.Scope).IsEqualTo(OutputScope.PerTeamPerRound);
        await Assert.That(string.Join(",", round.Dimensions)).IsEqualTo("match_id,map,round_number,side,slots");
        await Assert.That(match.Scope).IsEqualTo(OutputScope.PerTeamPerGame);
        await Assert.That(string.Join(",", match.Dimensions)).IsEqualTo("match_id,map,side");
    }

    // ── on the sample ─────────────────────────────────────────────────────

    /// <summary>
    ///     Two rows per round, one per side; the sides' kills add up to the round's kills; exactly one
    ///     side wins each decided round, the one round_decided named; each side's roster is its five
    ///     players at freeze end; a side's equipment is what each of its players reads, and its money
    ///     the sum of theirs; and round.team.* / round.enemies.* read the side and the other side,
    ///     not the other way round: a side's enemies are the other side's team, and at the round's
    ///     last kill the side that lost has no one alive.
    /// </summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_OneRowPerRoundPerSide_AgreesWithThePerPlayerTwin()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, PerSideRound, PerPlayerRound, """
            ruleset: decided
            for: match
            stats:
              winners:
                capture: event.Winner
                on: round_decided
                keep: list
                per: match
            """);

        MetricTable sides = RoundFactsTestSupport.Table(run, "sides", demo);
        MetricTable players = RoundFactsTestSupport.Table(run, "players", demo);
        int[] winners = RoundFactsCorpusTests.Ints(run, "decided.winners");

        List<IGrouping<int, MetricRow>> rounds = sides.Rows.GroupBy(r => RoundFactsTestSupport.Dim(r, "round_number")).ToList();
        await Assert.That(rounds.Count).IsGreaterThan(0);
        foreach (IGrouping<int, MetricRow> round in rounds)
        {
            await Assert.That(string.Join(",", round.Select(r => RoundFactsTestSupport.Dim(r, "side")))).IsEqualTo("2,3");

            List<MetricRow> roundPlayers = players.Rows.Where(r => RoundFactsTestSupport.Dim(r, "round_number") == round.Key).ToList();
            int playerKills = roundPlayers.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0);
            await Assert.That(round.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0)).IsEqualTo(playerKills)
                .Because($"round {round.Key}");

            // Exactly one side won, the one the server named.
            MetricRow winner = round.Single(r => RoundFactsTestSupport.Int(r, "W") == 1);
            await Assert.That(round.Count(r => RoundFactsTestSupport.Int(r, "L") == 1)).IsEqualTo(1);
            await Assert.That(RoundFactsTestSupport.Dim(winner, "side")).IsEqualTo(winners[round.Key - 1]);

            foreach (MetricRow side in round)
            {
                int sideNumber = RoundFactsTestSupport.Dim(side, "side");
                string because = $"round {round.Key} side {sideNumber}";
                MetricRow other = round.Single(r => RoundFactsTestSupport.Dim(r, "side") != sideNumber);
                await Assert.That(RoundFactsTestSupport.Int(side, "S")).IsEqualTo(sideNumber);

                int[] slots = (side.Dimensions["slots"]?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(int.Parse).ToArray();
                await Assert.That(slots.Length).IsEqualTo(5).Because(because);
                int memberMoney = 0;
                foreach (int slot in slots)
                {
                    MetricRow member = roundPlayers.Single(r => RoundFactsTestSupport.Dim(r, "player_slot") == slot);
                    await Assert.That(RoundFactsTestSupport.Int(member, "EQ")).IsEqualTo(RoundFactsTestSupport.Int(side, "EQ"));
                    memberMoney += RoundFactsTestSupport.Int(member, "M") ?? 0;
                }

                await Assert.That(RoundFactsTestSupport.Int(side, "M")).IsEqualTo(memberMoney).Because(because);

                // Five a side, all alive at freeze end.
                foreach (string column in (string[])["TP", "EP", "TA", "EA"])
                {
                    await Assert.That(RoundFactsTestSupport.Int(side, column)).IsEqualTo(5).Because($"{because} {column}");
                }

                // The enemies are the other side.
                await Assert.That(RoundFactsTestSupport.Int(side, "EEQ")).IsEqualTo(RoundFactsTestSupport.Int(other, "EQ"))
                    .Because(because);
                await Assert.That(RoundFactsTestSupport.Int(side, "EM")).IsEqualTo(RoundFactsTestSupport.Int(other, "M"))
                    .Because(because);
                string aliveAfterKill = side.Values["TAK"]?.ToString() ?? "";
                await Assert.That(aliveAfterKill).IsNotEmpty().Because(because);
                await Assert.That(aliveAfterKill).IsEqualTo(other.Values["EAK"]?.ToString()).Because(because);

                // Both rounds on the sample end in an elimination, so the loser's count reaches 0.
                bool lost = sideNumber != winners[round.Key - 1];
                await Assert.That(aliveAfterKill.EndsWith(",0", StringComparison.Ordinal)).IsEqualTo(lost).Because(because);
            }

            // The two sides' equipment differ on the sample, so a swapped read cannot pass above.
            await Assert.That(RoundFactsTestSupport.Int(round.First(), "EQ"))
                .IsNotEqualTo(RoundFactsTestSupport.Int(round.Last(), "EQ"));
        }

        MetricTable match = RoundFactsTestSupport.Table(run, "sides_match", demo);
        await Assert.That(match.Rows.Count).IsEqualTo(2);
        await Assert.That(match.Rows.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0))
            .IsEqualTo(players.Rows.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0));
    }

    /// <summary>A team-only build still tracks players: its side binding and rosters need them.</summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_TeamOnlyBuild_TracksPlayers()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, PerSideRound);
        MetricTable sides = RoundFactsTestSupport.Table(run, "sides", demo);

        await Assert.That(run.MaterializedPlayers.Count).IsEqualTo(0);
        await Assert.That(sides.Rows.Sum(r => RoundFactsTestSupport.Int(r, "K") ?? 0)).IsGreaterThan(0);
        await Assert.That(sides.Rows.All(r => r.Dimensions["slots"] is not null)).IsTrue();
    }

    /// <summary>A mixed build materializes each player once, however many scopes it has.</summary>
    [Test]
    [Category("Integration")]
    [NotInParallel]
    public async Task Sample_MixedBuild_MaterializesEachSlotOnce()
    {
        ParsedDemo demo = RoundFactsTestSupport.Sample();
        AnalysisRun run = RoundFactsTestSupport.Run(demo, PerSideRound, PerPlayerRound);
        List<int> slots = run.MaterializedPlayers.Select(p => p.PlayerSlot).ToList();
        await Assert.That(slots.Count).IsEqualTo(slots.Distinct().Count());
        await Assert.That(slots.Count).IsEqualTo(10);
    }

    /// <summary>The rosters swap at halftime: a side's slots in round 13 are the other side's in round 12.</summary>
    [Test]
    [Category("Corpus")]
    [NotInParallel]
    public async Task Corpus_RostersSwapAtHalftime()
    {
        string path = DemoTestHelper.RequireDemo(GameRulesProviderTests.Build10231Nuke);
        ParsedDemo demo = DemoTestHelper.GetOrParse(path);
        AnalysisRun run = RoundFactsTestSupport.Run(demo, PerSideRound);
        MetricTable sides = RoundFactsTestSupport.Table(run, "sides", demo);

        string Slots(int round, int side) =>
            sides.Rows.Single(r => RoundFactsTestSupport.Dim(r, "round_number") == round && RoundFactsTestSupport.Dim(r, "side") == side)
                .Dimensions["slots"]?.ToString() ?? "";

        await Assert.That(Slots(13, 2)).IsEqualTo(Slots(12, 3));
        await Assert.That(Slots(13, 3)).IsEqualTo(Slots(12, 2));
        await Assert.That(Slots(12, 2)).IsNotEqualTo(Slots(12, 3));
    }
}
