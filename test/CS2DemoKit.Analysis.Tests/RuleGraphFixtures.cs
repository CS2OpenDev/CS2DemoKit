#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Profiles;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The builds the rule-graph tests share: the shipped rulesets on the GOTV and HLTV profiles, and
///     an inline matrix that reaches every stat kind, every gate shape and all three ruleset scopes
///     the planner lowers. All demo-less, at 64 ticks, with the default provider registries.
/// </summary>
internal static class RuleGraphFixtures
{
    /// <summary>
    ///     The <c>for: each_player</c> third of the matrix. It names each stat kind once and each gate
    ///     shape once: a net-message count, a round-end and a live compute, a flag on an event and on a
    ///     condition (the <c>player.health</c> one takes the settle-site pull and settle edge),
    ///     streak, burst, a bucket pair and a rate over it, a tally, a reflective B6 <c>while:</c>, an
    ///     entity-fold <c>while:</c>, the freeze-end economy, the bomb-site round fact and a highlight.
    /// </summary>
    internal const string MatrixPlayerYaml =
        """
        ruleset: fm_player
        for: each_player
        stats:
          kills:
            count: kill
            per: round
          kills_match:
            count: kill
            per: match
          headers:
            count: net.CDemoFileHeader
            per: match
          dmg:
            sum: event.DmgHealth
            on: damage_dealt
            per: round
          victims:
            capture: event.UserId
            on: kill
            keep: list
            per: round
          min_dmg:
            capture: event.DmgHealth
            on: damage_dealt
            keep: min
            per: round
          max_dmg:
            capture: event.DmgHealth
            on: damage_dealt
            keep: max
            per: match
          first_dmg:
            capture: event.DmgHealth
            on: damage_dealt
            keep: first
            per: round
          last_dmg:
            capture: event.DmgHealth
            on: damage_dealt
            per: round
          per_round:
            compute: "kills_match / round.number"
            per: match
          live_kills:
            compute: { value: kills, live: true }
            per: round
          first_kill_round:
            flag: true
            on: kill
            while: round.no_deaths_yet
            per: round
          had_kill:
            flag:
              when: "kills > 0"
            per: round
          healthy:
            flag:
              when: "player.health > 50"
            per: round
          streaky:
            streak: kill
            window: 640
            min_streak: 2
            per: match
          bursty:
            burst: kill
            window: 320
            min_streak: 2
            per: round
          by_weapon:
            bucket: kill
            key: event.Weapon
            per: match
          enemy_by_weapon:
            bucket: kill
            key: event.Weapon
            match: { enemy: true }
            per: match
          enemy_rate:
            rate: { of: enemy_by_weapon, per: by_weapon }
            per: match
          multi_tally:
            tally: kills
            thresholds:
              - { min: 3, target: rounds_3k }
              - { min: 2, target: rounds_2k }
            per: match
          clutch_kills:
            count: kill
            while: round.alive.in_clutch
            per: match
          rich_kills:
            count: kill
            while: "round.team.equipment > 2000"
            per: match
          cash:
            capture: round.team.money
            on: raw.round_freeze_end
            per: round
          healthy_kills:
            count: kill
            while: "player.health > 50"
            per: round
          site:
            capture: round.bomb.site
            on: round_ended
            per: round
        highlights:
          multi:
            when: kills >= 2
            per: round
            title: "multi for {player.name}"
        """;

    /// <summary>The <c>for: match</c> third of the matrix.</summary>
    internal const string MatrixMatchYaml =
        """
        ruleset: fm_match
        for: match
        stats:
          kills:
            count: kill
            per: match
          plant_at:
            capture: event.tick
            on: bomb_planted
            keep: list
            per: match
          kills_per_round:
            compute: "kills / round.number"
            per: match
        """;

    /// <summary>The <c>for: each_team</c> third of the matrix.</summary>
    internal const string MatrixSidesYaml =
        """
        ruleset: fm_sides
        for: each_team
        stats:
          kills:
            count: kill
            per: round
          won:
            count: round_won
            per: round
          money:
            capture: round.team.money
            on: raw.round_freeze_end
            per: round
          alive_kills:
            count: kill
            while: "round.team.alive > 1"
            per: round
        """;

    /// <summary>The shipped rulesets.</summary>
    internal static IReadOnlyList<RulesetDoc> Shipped() => YamlConfigLoader.LoadShippedEmbedded().Rulesets;

    /// <summary>The matrix rulesets, mapped; throws when any document fails to map.</summary>
    internal static IReadOnlyList<RulesetDoc> Matrix()
    {
        // One ruleset per document: LoadDocuments maps a document to a single ruleset, so a '---'
        // separated second ruleset in the same text would not load.
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments(
        [
            ("fm_player.rules.yaml", MatrixPlayerYaml),
            ("fm_match.rules.yaml", MatrixMatchYaml),
            ("fm_sides.rules.yaml", MatrixSidesYaml)
        ]);
        if (loaded.Errors.Count > 0 || loaded.Rulesets.Count != 3)
        {
            throw new InvalidOperationException("matrix failed to load: " + string.Join("; ", loaded.Errors));
        }

        return loaded.Rulesets;
    }

    /// <summary>Every example under <c>Rules/examples/</c>, by file name.</summary>
    internal static IEnumerable<string> ExampleFiles()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CS2DemoKit.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        string examples = Path.Combine(dir ?? throw new InvalidOperationException("repo root not found"),
            "src", "CS2DemoKit.Analysis", "Rules", "examples");
        return Directory.EnumerateFiles(examples, "*.rules.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal);
    }

    /// <summary>
    ///     The example at <paramref name="path" /> on its own. <see cref="TallyTargetScopeTests" /> runs
    ///     them all beside the shipped rulesets.
    /// </summary>
    internal static IReadOnlyList<RulesetDoc> Example(string path)
    {
        RuleConfigLoadResult loaded = YamlConfigLoader.LoadDocuments([(Path.GetFileName(path), File.ReadAllText(path))]);
        if (loaded.Errors.Count > 0)
        {
            throw new InvalidOperationException("example failed to load: " + string.Join("; ", loaded.Errors));
        }

        return loaded.Rulesets;
    }

    /// <summary>
    ///     Builds <paramref name="docs" /> for <paramref name="profile" /> at 64 ticks, and fails loudly
    ///     when composition dropped anything: a build that silently lost a ruleset would pin less than
    ///     it claims to.
    /// </summary>
    internal static BuildResult Build(IReadOnlyList<RulesetDoc> docs, DemoSourceProfile profile)
    {
        BuildResult build = DemoAnalysis.Build(new AnalysisTarget(64, profile), docs);
        if (build.ExcludedRulesets.Count > 0)
        {
            throw new InvalidOperationException("composition dropped rulesets: " + string.Join("; ",
                build.RulesetDiagnostics.Select(d => d.ToString())));
        }

        return build;
    }

    /// <summary>The named builds the pinned tests run over.</summary>
    internal static IEnumerable<(string Name, Func<BuildResult> Build)> Cases()
    {
        yield return ("shipped-gotv", () => Build(Shipped(), new Cs2GotvProfile()));
        yield return ("shipped-hltv", () => Build(Shipped(), new Cs2HltvProfile()));
        yield return ("matrix-gotv", () => Build(Matrix(), new Cs2GotvProfile()));
        yield return ("matrix-hltv", () => Build(Matrix(), new Cs2HltvProfile()));
    }

    /// <summary>The pinned cases, plus each example on GOTV.</summary>
    internal static IEnumerable<(string Name, Func<BuildResult> Build)> CoverageCases() =>
        Cases().Concat(ExampleFiles().Select(path => (
            "example-" + Path.GetFileName(path).Replace(".rules.yaml", "", StringComparison.Ordinal),
            (Func<BuildResult>)(() => Build(Example(path), new Cs2GotvProfile())))));
}
