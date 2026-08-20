#region

using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The id set a <c>show:</c> reference may name, built once from a
///     <see cref="CheckedRuleset" /> and shared by the validator and <c>ShowLowering</c> so the
///     two cannot disagree about what resolves.
///     <para>
///         Lives in <c>Resolve</c> rather than <c>Compile</c> because the validator runs during
///         composition, before any node map exists. <c>ShowLowering</c> already depends on
///         <c>Resolve</c>, so this direction is the one that does not cycle.
///     </para>
/// </summary>
public sealed class ShowReferenceIds
{
    private const string CountSuffix = ".count";

    private readonly HashSet<string> _coverageSkipped;
    private readonly HashSet<string> _highlights;
    private readonly Dictionary<string, CheckedStat> _stats;

    // Target id -> the tally that emits it, kept because the board a scoreboard column defaults to
    // follows the owning tally's scope.
    private readonly Dictionary<string, CheckedStat> _tallyOwners;

    private ShowReferenceIds(
        Dictionary<string, CheckedStat> stats, HashSet<string> highlights,
        Dictionary<string, CheckedStat> tallyOwners, HashSet<string> coverageSkipped)
    {
        _stats = stats;
        _highlights = highlights;
        _tallyOwners = tallyOwners;
        _coverageSkipped = coverageSkipped;
    }

    /// <summary>Collects the referencable ids of <paramref name="ruleset" />.</summary>
    /// <param name="ruleset">The checked ruleset whose show block is being resolved or validated.</param>
    /// <returns>The id set.</returns>
    public static ShowReferenceIds From(CheckedRuleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);

        Dictionary<string, CheckedStat> stats = new(StringComparer.Ordinal);
        // A tally: stat produces no node under its own id. Its thresholds emit under their target:
        // ids, which are what a show ref names (spec §6 tally row).
        Dictionary<string, CheckedStat> tallyOwners = new(StringComparer.Ordinal);
        foreach (CheckedStat stat in ruleset.Stats)
        {
            stats[stat.StatId] = stat;
            if (stat.TallyThresholds is { } thresholds)
            {
                foreach ((int _, string target) in thresholds)
                {
                    tallyOwners[target] = stat;
                }
            }
        }

        return new ShowReferenceIds(
            stats,
            new HashSet<string>(ruleset.Highlights.Select(h => h.HighlightId), StringComparer.Ordinal),
            tallyOwners,
            new HashSet<string>(ruleset.Coverage.Select(c => c.NodeId), StringComparer.Ordinal));
    }

    /// <summary>
    ///     Whether <paramref name="reference" /> names a stat, a highlight (bare or <c>.count</c>),
    ///     or a tally target.
    /// </summary>
    /// <param name="reference">The show reference as written.</param>
    /// <returns><c>true</c> when the reference resolves.</returns>
    public bool Resolves(string reference) =>
        TryResolveHighlight(reference, out _)
        || _stats.ContainsKey(reference)
        || _tallyOwners.ContainsKey(reference);

    /// <summary>
    ///     Resolves a highlight reference, written either bare or with an explicit <c>.count</c>.
    /// </summary>
    /// <param name="reference">The show reference as written.</param>
    /// <param name="highlightId">The highlight id, with any <c>.count</c> stripped.</param>
    /// <returns><c>true</c> when the reference names a highlight.</returns>
    public bool TryResolveHighlight(string reference, out string highlightId)
    {
        highlightId = StripCount(reference);
        return _highlights.Contains(highlightId);
    }

    /// <summary>Looks up a stat by its declared id.</summary>
    /// <param name="id">The stat id.</param>
    /// <param name="stat">The stat, when declared.</param>
    /// <returns><c>true</c> when the ruleset declares a stat under <paramref name="id" />.</returns>
    public bool TryGetStat(string id, out CheckedStat? stat) => _stats.TryGetValue(id, out stat);

    /// <summary>Looks up the tally that emits a given target id.</summary>
    /// <param name="targetId">The tally threshold's <c>target:</c> id.</param>
    /// <param name="owner">The owning tally stat, when the target exists.</param>
    /// <returns><c>true</c> when some tally emits <paramref name="targetId" />.</returns>
    public bool TryGetTallyOwner(string targetId, out CheckedStat? owner) =>
        _tallyOwners.TryGetValue(targetId, out owner);

    /// <summary>
    ///     Whether <paramref name="reference" /> names a node dropped as a coverage skip. Those are
    ///     not built, so a ref to one is a legitimate outcome the coverage diagnostic already
    ///     reported, and neither lowering nor validation may treat it as an error.
    /// </summary>
    /// <param name="reference">The show reference as written.</param>
    /// <returns><c>true</c> when the referent was coverage-skipped.</returns>
    public bool IsCoverageSkipped(string reference) => _coverageSkipped.Contains(StripCount(reference));

    private static string StripCount(string reference) =>
        reference.EndsWith(CountSuffix, StringComparison.Ordinal)
            ? reference[..^CountSuffix.Length]
            : reference;
}

/// <summary>
///     Checks that every <c>show:</c> reference names something the ruleset declares, and that each
///     table's <c>per:</c> is a supported dimension.
///     <para>
///         Without this the demo-less check passes clean and the problem surfaces later, differently
///         on each path: a bad <c>scoreboard:</c> ref throws from <c>ShowLowering</c> at build, while
///         a bad <c>tables:</c> column resolves to no metric node and projects an all-null column
///         with nothing reported. An unsupported <c>per:</c> throws at build.
///     </para>
///     <para>
///         Only the existence half of the build's check lives here. Resolving a ref to a compiled
///         node still happens in <c>ShowLowering</c>, which has the node map; that throw stays as a
///         defensive invariant.
///     </para>
/// </summary>
public static class ShowReferenceValidator
{
    private static readonly string[] _supportedPer = ["player_round", "player_match", "match"];

    /// <summary>Validates <paramref name="ruleset" />'s show block.</summary>
    /// <param name="ruleset">The resolved ruleset.</param>
    /// <returns>One diagnostic per bad reference or dimension; empty when the show block is sound.</returns>
    public static IReadOnlyList<RulesetDiagnostic> Validate(CheckedRuleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        if (ruleset.Show is not { } show)
        {
            return [];
        }

        ShowReferenceIds ids = ShowReferenceIds.From(ruleset);
        List<RulesetDiagnostic> diagnostics = [];

        foreach (ScoreboardEntry entry in show.Scoreboard)
        {
            if (!ids.IsCoverageSkipped(entry.Stat) && !ids.Resolves(entry.Stat))
            {
                diagnostics.Add(new RulesetDiagnostic(
                    ResolveDiagnosticCodes.ShowUnknownRef,
                    $"show: scoreboard entry '{entry.Stat}' in ruleset '{ruleset.Id.Id}' references neither a "
                    + "stat, a highlight, nor a tally target defined in the ruleset.",
                    entry.Position));
            }
        }

        foreach (TableDef table in show.Tables)
        {
            if (!_supportedPer.Contains(table.Per, StringComparer.Ordinal))
            {
                diagnostics.Add(new RulesetDiagnostic(
                    ResolveDiagnosticCodes.ShowBadTableDimension,
                    $"show: table '{table.Name}' in ruleset '{ruleset.Id.Id}' declares 'per: "
                    + $"{table.Per ?? "<missing>"}', which is not a supported table dimension "
                    + $"({string.Join(" | ", _supportedPer)}).",
                    table.Position));
            }

            foreach (TableColumn column in table.Columns)
            {
                if (!ids.IsCoverageSkipped(column.Stat) && !ids.Resolves(column.Stat))
                {
                    diagnostics.Add(new RulesetDiagnostic(
                        ResolveDiagnosticCodes.ShowUnknownRef,
                        $"show: table '{table.Name}' column '{column.Stat}' in ruleset '{ruleset.Id.Id}' "
                        + "references neither a stat, a highlight, nor a tally target defined in the ruleset.",
                        column.Position));
                }
            }
        }

        return diagnostics;
    }
}
