#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The cross-ruleset export graph: the directory's rulesets
///     with the structure a qualified <c>ruleset.stat</c> read resolves and is validated against —
///     each ruleset's materialization scope (for the read-scope rule), its full declared-id set (to
///     attribute an unknown-stat error apart from a not-exported one), and its <b>exported</b> stats
///     with their value types (the scope roots a <c>use:</c>-ing ruleset resolves reads against).
///     Built after all documents parse (<see cref="RulesetComposition" />); the resolver's
///     <see cref="RulesetResolver" /> consumes <see cref="TryGetExportedStats" /> to add used-ruleset
///     namespaces to scope, and <see cref="CrossRulesetReferenceValidator" /> consumes the rest to
///     emit the four attributed errors + the read-scope error.
/// </summary>
public sealed class RulesetExportGraph
{
    private readonly Dictionary<string, Entry> _byRuleset;

    /// <summary>Creates an export graph from a per-ruleset entry table.</summary>
    /// <param name="entries">Ruleset id → its export entry.</param>
    public RulesetExportGraph(IReadOnlyDictionary<string, Entry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _byRuleset = new Dictionary<string, Entry>(entries, StringComparer.Ordinal);
    }

    /// <summary>Every ruleset id the directory knows.</summary>
    public IReadOnlyCollection<string> RulesetIds => _byRuleset.Keys;

    /// <summary>True when the directory contains a ruleset with this id.</summary>
    public bool ContainsRuleset(string ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return _byRuleset.ContainsKey(ruleset);
    }

    /// <summary>Looks up a ruleset's full export entry.</summary>
    public bool TryGetRuleset(string ruleset, out Entry entry)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        return _byRuleset.TryGetValue(ruleset, out entry!);
    }

    /// <summary>
    ///     Looks up a ruleset's <b>exported</b> stats with their value types — the scope roots a
    ///     <c>use:</c>-ing ruleset resolves qualified reads against.
    /// </summary>
    /// <param name="ruleset">The ruleset id.</param>
    /// <param name="exported">The exported stat → type table when the ruleset is known.</param>
    /// <returns>True when the ruleset is in the graph.</returns>
    public bool TryGetExportedStats(string ruleset,
        out IReadOnlyDictionary<string, RulesType>? exported)
    {
        ArgumentNullException.ThrowIfNull(ruleset);
        if (_byRuleset.TryGetValue(ruleset, out Entry entry))
        {
            exported = entry.ExportedStatTypes;
            return true;
        }

        exported = null;
        return false;
    }

    /// <summary>
    ///     One ruleset's export surface: its materialization scope, every declared stat/highlight id
    ///     (existence set), the exported-id set (the subset visible to other rulesets), and the typed
    ///     exported stats (exported ids that resolve to a concrete value type).
    /// </summary>
    /// <param name="For">The ruleset's <c>for:</c> scope (read-scope rule input).</param>
    /// <param name="DeclaredIds">Every declared stat + highlight id — the unknown-stat attribution set.</param>
    /// <param name="ExportedIds">The exported subset of ids (all declared when <c>exports:</c> was absent).</param>
    /// <param name="ExportedStatTypes">Exported stat ids that resolved to a value type — the scope roots.</param>
    public readonly record struct Entry(
        RulesetScope For,
        IReadOnlySet<string> DeclaredIds,
        IReadOnlySet<string> ExportedIds,
        IReadOnlyDictionary<string, RulesType> ExportedStatTypes);
}
