#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The cross-ruleset stat-reference cycle pre-pass: the directory-level
///     analogue of <see cref="StatReferenceCycleDetector" />. It builds one dependency graph over
///     every document's declared stats/highlights — keyed by their qualified <c>{ruleset}.{id}</c>
///     spelling — from the <b>parsed reference structure</b> of each expression, following a bare
///     read to a sibling of the reading ruleset and a qualified <c>ruleset.stat</c> read to the
///     ruleset it names. Working from structure (not from resolved types) is deliberate: a genuine
///     mutual cross-ruleset read prevents either ruleset from type-resolving, so it must be caught
///     here, structurally, rather than surfacing as an unresolved-reference error. A cycle spanning
///     two or more rulesets is a build error naming the cycle; within-ruleset cycles are the
///     per-ruleset detector's job and are filtered out here.
/// </summary>
public static class CrossRulesetCycleDetector
{
    /// <summary>Detects cross-ruleset stat-reference cycles across the directory's documents.</summary>
    /// <param name="docs">Every document in the directory.</param>
    /// <returns>One diagnostic per detected cross-ruleset cycle (naming its qualified path), or empty.</returns>
    public static IReadOnlyList<RulesetDiagnostic> Detect(IReadOnlyList<RulesetDoc> docs)
    {
        ArgumentNullException.ThrowIfNull(docs);

        HashSet<string> rulesetIds = new(docs.Select(d => d.Id), StringComparer.Ordinal);
        HashSet<string> nodes = new(StringComparer.Ordinal);
        Dictionary<string, SourcePosition> positions = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> localIdsByRuleset = new(StringComparer.Ordinal);

        foreach (RulesetDoc doc in docs)
        {
            HashSet<string> local = new(StringComparer.Ordinal);
            foreach (StatDef stat in doc.Stats)
            {
                string key = Key(doc.Id, stat.Id);
                nodes.Add(key);
                positions[key] = stat.Position;
                local.Add(stat.Id);
            }

            foreach (HighlightDef highlight in doc.Highlights)
            {
                string key = Key(doc.Id, highlight.Id);
                nodes.Add(key);
                positions[key] = highlight.Position;
                local.Add(highlight.Id);
            }

            localIdsByRuleset[doc.Id] = local;
        }

        Dictionary<string, List<string>> edges = new(StringComparer.Ordinal);
        foreach (string node in nodes)
        {
            edges[node] = [];
        }

        foreach (RulesetDoc doc in docs)
        {
            AddDocEdges(doc, rulesetIds, localIdsByRuleset[doc.Id], nodes, edges);
        }

        return FindCrossRulesetCycles(edges, positions);
    }

    private static string Key(string ruleset, string id) => $"{ruleset}.{id}";

    private static void AddDocEdges(RulesetDoc doc, HashSet<string> rulesetIds, HashSet<string> localIds,
        HashSet<string> nodes, Dictionary<string, List<string>> edges)
    {
        // Map every expression back to the id whose node it belongs to (a stat's slots to the stat, a
        // highlight's when: to the highlight), so a read becomes an edge from the right source node.
        foreach (StatDef stat in doc.Stats)
        {
            string source = Key(doc.Id, stat.Id);
            foreach ((string text, SourcePosition _) in StatExpressions(stat))
            {
                AddReadEdges(text, doc.Id, source, rulesetIds, localIds, nodes, edges);
            }
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            string source = Key(doc.Id, highlight.Id);
            AddReadEdges(highlight.When, doc.Id, source, rulesetIds, localIds, nodes, edges);
        }
    }

    private static IEnumerable<(string Text, SourcePosition Pos)> StatExpressions(StatDef stat)
    {
        // The same expression surface CrossRulesetReferenceValidator enumerates, but restricted to the
        // one stat (so the source node is correct). Reuse its collector for the reference walk below.
        if (stat.KindArg is { } kindArg)
        {
            yield return (kindArg, stat.Position);
        }

        foreach ((string Text, SourcePosition Pos) slot in TriggerExpressions(stat.Trigger))
        {
            yield return slot;
        }

        foreach ((string Text, SourcePosition Pos) slot in TriggerExpressions(stat.OffTrigger))
        {
            yield return slot;
        }

        if (stat.BucketKey is { } key)
        {
            yield return (key, stat.Position);
        }

        if (stat.BucketValue is { } value)
        {
            yield return (value, stat.Position);
        }

        if (stat.BucketKeys is { } keys)
        {
            foreach (string part in keys)
            {
                yield return (part, stat.Position);
            }
        }
    }

    private static IEnumerable<(string Text, SourcePosition Pos)> TriggerExpressions(TriggerDef? trigger)
    {
        if (trigger is null)
        {
            yield break;
        }

        if (trigger.Where is { } where)
        {
            yield return (where, trigger.Position);
        }

        if (trigger.While is { } gate)
        {
            yield return (gate, trigger.Position);
        }
    }

    private static void AddReadEdges(string text, string owningRuleset, string source, HashSet<string> rulesetIds,
        HashSet<string> localIds, HashSet<string> nodes, Dictionary<string, List<string>> edges)
    {
        LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(text);
        if (!parsed.Success)
        {
            return;
        }

        foreach (ReferenceNode reference in CrossRulesetReferenceValidator.CollectReferences(parsed.Require()))
        {
            string head = reference.Segments[0];
            string? target = null;

            if (localIds.Contains(head))
            {
                target = Key(owningRuleset, head); // a bare sibling read (a highlight's <id>.count too)
            }
            else if (reference.Segments.Length >= 2 && rulesetIds.Contains(head)
                                                    && !string.Equals(head, owningRuleset, StringComparison.Ordinal))
            {
                target = Key(head, reference.Segments[1]); // a qualified cross-ruleset read
            }

            if (target is not null && nodes.Contains(target) && !edges[source].Contains(target))
            {
                edges[source].Add(target);
            }
        }
    }

    private static List<RulesetDiagnostic> FindCrossRulesetCycles(Dictionary<string, List<string>> edges,
        Dictionary<string, SourcePosition> positions)
    {
        List<RulesetDiagnostic> diagnostics = new();
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

        // Only a cycle spanning two or more rulesets is a cross-ruleset cycle; a within-ruleset cycle
        // is the per-ruleset detector's job.
        List<string> rulesetsInCycle = cyclePath
            .Select(n => n[..n.IndexOf('.', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (rulesetsInCycle.Count < 2)
        {
            return;
        }

        string canonicalKey = string.Join(",", cyclePath.OrderBy(n => n, StringComparer.Ordinal));
        if (!reported.Add(canonicalKey))
        {
            return;
        }

        SourcePosition pos = positions.TryGetValue(node, out SourcePosition p) ? p : SourcePosition.None;
        diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.CrossRefCycle,
            $"cross-ruleset stat-reference cycle: {string.Join(" -> ", cyclePath)}", pos));
    }

    private enum Mark
    {
        InProgress,
        Done
    }
}
