#region

using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The within-ruleset stat-reference cycle pre-pass (spec §6
///     cycle rule). The per-expression checker cannot see cross-stat cycles, so this walks the
///     resolved-reference sets of <b>every</b> checked expression a node carries — the trigger
///     condition, the value selector, the <c>while:</c> gate, and a highlight's <c>when:</c> — and
///     builds the stat-reference dependency graph. A cycle is a build error naming the cycle path;
///     catching it here is what makes the planner's dependency-ordered hashing terminate. The
///     <c>this</c> self-reference is a non-stat Value symbol, so it never contributes an edge; a
///     highlight's automatic <c>&lt;id&gt;.count</c> node is walked as a first-class node (a read of
///     it is a stat reference, and it depends on its highlight).
/// </summary>
public static class StatReferenceCycleDetector
{
    /// <summary>Detects within-ruleset stat-reference cycles.</summary>
    /// <param name="ruleset">The checked ruleset.</param>
    /// <returns>One diagnostic per detected cycle (naming its path), or empty when acyclic.</returns>
    public static IReadOnlyList<RulesetDiagnostic> Detect(CheckedRuleset ruleset)
    {
        ArgumentNullException.ThrowIfNull(ruleset);

        // Node set: every stat id, every highlight id, and every highlight's <id>.count node.
        HashSet<string> nodes = new(StringComparer.Ordinal);
        Dictionary<string, SourcePosition> positions = new(StringComparer.Ordinal);
        foreach (CheckedStat stat in ruleset.Stats)
        {
            nodes.Add(stat.StatId);
            positions[stat.StatId] = stat.Position;
        }

        foreach (CheckedHighlight highlight in ruleset.Highlights)
        {
            nodes.Add(highlight.HighlightId);
            nodes.Add(highlight.CountNodeId);
            positions[highlight.HighlightId] = highlight.Position;
            positions.TryAdd(highlight.CountNodeId, highlight.Position);
        }

        // Edges: A → B means "A reads stat/count B".
        Dictionary<string, List<string>> edges = new(StringComparer.Ordinal);
        foreach (string node in nodes)
        {
            edges[node] = [];
        }

        foreach (CheckedStat stat in ruleset.Stats)
        {
            AddEdges(edges, nodes, stat.StatId, stat.TriggerCondition, stat.ValueSelector, stat.WhileGate);
        }

        foreach (CheckedHighlight highlight in ruleset.Highlights)
        {
            AddEdges(edges, nodes, highlight.HighlightId, highlight.When);

            // The auto <id>.count node is produced by the highlight's rising edge, so it depends on it.
            edges[highlight.CountNodeId].Add(highlight.HighlightId);
        }

        return FindCycles(edges, positions);
    }

    private static void AddEdges(Dictionary<string, List<string>> edges, HashSet<string> nodes, string source,
        params CheckedExpression?[] expressions)
    {
        foreach (CheckedExpression? expression in expressions)
        {
            if (expression is null)
            {
                continue;
            }

            foreach (ResolvedReference reference in expression.References)
            {
                if (reference is { IsStatReference: true, StatPath: { } target } && nodes.Contains(target)
                                                                                 && !edges[source].Contains(target))
                {
                    edges[source].Add(target);
                }
            }
        }
    }

    private static List<RulesetDiagnostic> FindCycles(Dictionary<string, List<string>> edges,
        Dictionary<string, SourcePosition> positions)
    {
        List<RulesetDiagnostic> diagnostics = [];
        Dictionary<string, Mark> state = new(StringComparer.Ordinal);
        List<string> stack = new();
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach (string node in edges.Keys.OrderBy(n => n, StringComparer.Ordinal))
        {
            Visit(node, edges, state, stack, positions, diagnostics, reported);
        }

        return diagnostics;
    }

    private static void Visit(string node, Dictionary<string, List<string>> edges,
        Dictionary<string, Mark> state, List<string> stack, Dictionary<string, SourcePosition> positions,
        List<RulesetDiagnostic> diagnostics, HashSet<string> reported)
    {
        if (state.TryGetValue(node, out Mark mark))
        {
            if (mark == Mark.InProgress)
            {
                ReportCycle(node, stack, positions, diagnostics, reported);
            }

            return;
        }

        state[node] = Mark.InProgress;
        stack.Add(node);
        foreach (string next in edges[node])
        {
            Visit(next, edges, state, stack, positions, diagnostics, reported);
        }

        stack.RemoveAt(stack.Count - 1);
        state[node] = Mark.Done;
    }

    private static void ReportCycle(string node, List<string> stack, Dictionary<string, SourcePosition> positions,
        List<RulesetDiagnostic> diagnostics, HashSet<string> reported)
    {
        int start = stack.LastIndexOf(node);
        if (start < 0)
        {
            return;
        }

        List<string> cyclePath = [.. stack[start..], node];
        string canonicalKey = string.Join(",", cyclePath.OrderBy(n => n, StringComparer.Ordinal));
        if (!reported.Add(canonicalKey))
        {
            return;
        }

        SourcePosition pos = positions.TryGetValue(node, out SourcePosition p) ? p : SourcePosition.None;
        diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.StatReferenceCycle,
            $"stat-reference cycle: {string.Join(" -> ", cyclePath)}", pos));
    }

    private enum Mark
    {
        InProgress,
        Done
    }
}
