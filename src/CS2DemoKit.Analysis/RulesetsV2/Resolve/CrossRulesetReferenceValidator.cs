#region

using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The cross-ruleset reference validator: a directory-level pass that
///     classifies every qualified <c>ruleset.stat</c> read in a document against the
///     <see cref="RulesetExportGraph" /> and emits the <b>four attributed errors</b> — not-in-use,
///     unknown-ruleset, unknown-stat, not-exported — plus the read-scope error (a match-scoped
///     ruleset may not read a per-player ruleset's stat). It is deliberately separate from the
///     per-slot type checker: the checker adds a used ruleset's <em>exported</em> stats as scope
///     roots and resolves a legal read; this pass supplies the attribution the checker's generic
///     out-of-scope / unknown-member diagnostics cannot, and <see cref="RulesetComposition" /> runs
///     it up-front so an attributed cross-ref error short-circuits the (noisier) full resolve.
///     <para>
///         A qualified read is one whose head names another directory ruleset (or a
///         <c>use:</c>-listed name) and is not the document's own id or a local declared id — a
///         sibling <c>&lt;highlight&gt;.count</c> read is therefore never mis-attributed even when
///         the highlight shares the document's ruleset id.
///     </para>
/// </summary>
public static class CrossRulesetReferenceValidator
{
    /// <summary>Validates a document's qualified reads against the export graph.</summary>
    /// <param name="doc">The (expanded) document to validate.</param>
    /// <param name="graph">The directory export graph.</param>
    /// <returns>One diagnostic per distinct offending qualified read; empty when every read is legal.</returns>
    public static IReadOnlyList<RulesetDiagnostic> Validate(RulesetDoc doc, RulesetExportGraph graph)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(graph);

        HashSet<string> use = new(doc.Use, StringComparer.Ordinal);
        HashSet<string> localIds = CollectLocalIds(doc);
        List<RulesetDiagnostic> diagnostics = new();
        HashSet<string> reported = new(StringComparer.Ordinal);

        foreach ((string text, SourcePosition pos) in EnumerateExpressions(doc))
        {
            LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(text);
            if (!parsed.Success)
            {
                continue; // parse errors are the per-slot resolver's to report
            }

            foreach (ReferenceNode reference in CollectReferences(parsed.Require()))
            {
                Classify(reference, doc, use, localIds, graph, pos, diagnostics, reported);
            }
        }

        return diagnostics;
    }

    private static void Classify(ReferenceNode reference, RulesetDoc doc, HashSet<string> use,
        HashSet<string> localIds, RulesetExportGraph graph, SourcePosition pos,
        List<RulesetDiagnostic> diagnostics, HashSet<string> reported)
    {
        if (reference.Segments.Length < 2)
        {
            return;
        }

        string head = reference.Segments[0];
        string stat = reference.Segments[1];

        // Not a cross-ruleset read: the document's own id, a local declared id (a sibling stat /
        // highlight / param / define — a `<highlight>.count` read looks qualified but is local), or a
        // head that is neither use:-listed nor a directory ruleset (a normal scope root like round.*).
        if (string.Equals(head, doc.Id, StringComparison.Ordinal) || localIds.Contains(head))
        {
            return;
        }

        bool candidate = use.Contains(head) || graph.ContainsRuleset(head);
        if (!candidate)
        {
            return;
        }

        if (!reported.Add($"{head}.{stat}"))
        {
            return; // report each distinct qualified read once
        }

        if (!use.Contains(head))
        {
            diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.CrossRefNotInUse,
                $"'{head}.{stat}' reads ruleset '{head}', which is not in this document's use: allowlist — "
                + $"add '{head}' to use:", pos));
            return;
        }

        if (!graph.TryGetRuleset(head, out RulesetExportGraph.Entry entry))
        {
            diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.CrossRefUnknownRuleset,
                $"'{head}.{stat}' reads ruleset '{head}', which no document in the directory declares", pos));
            return;
        }

        if (!entry.DeclaredIds.Contains(stat))
        {
            diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.CrossRefUnknownStat,
                $"'{head}.{stat}' reads stat '{stat}', which ruleset '{head}' does not declare", pos));
            return;
        }

        if (!entry.ExportedIds.Contains(stat))
        {
            diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.CrossRefNotExported,
                $"'{head}.{stat}' reads stat '{stat}', which ruleset '{head}' declares but does not export "
                + "(add it to that ruleset's exports:)", pos));
            return;
        }

        // Read-scope rule: same-scope and per-player→match reads are legal; a match→per-player
        // read is an error — no player binding exists at match scope.
        if (doc.For == RulesetScope.Match && entry.For == RulesetScope.EachPlayer)
        {
            diagnostics.Add(new RulesetDiagnostic(ResolveDiagnosticCodes.CrossRefReadScope,
                $"match-scoped ruleset '{doc.Id}' may not read per-player stat '{head}.{stat}' — "
                + "no player binding exists at match scope", pos));
        }
    }

    private static HashSet<string> CollectLocalIds(RulesetDoc doc)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (StatDef stat in doc.Stats)
        {
            ids.Add(stat.Id);
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            ids.Add(highlight.Id);
        }

        foreach (ParamDef param in doc.Params)
        {
            ids.Add(param.Name);
        }

        foreach (DefineDef define in doc.Defines)
        {
            ids.Add(define.Name);
        }

        return ids;
    }

    /// <summary>Every raw expression string a stat/highlight carries, paired with its position.</summary>
    internal static IEnumerable<(string Text, SourcePosition Pos)> EnumerateExpressions(RulesetDoc doc)
    {
        foreach (StatDef stat in doc.Stats)
        {
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

        foreach (HighlightDef highlight in doc.Highlights)
        {
            yield return (highlight.When, highlight.Position);
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

    /// <summary>Depth-first collect of every <see cref="ReferenceNode" /> reachable in an AST.</summary>
    internal static IEnumerable<ReferenceNode> CollectReferences(ExpressionNode node)
    {
        switch (node)
        {
            case ReferenceNode reference:
                yield return reference;

                break;
            case MemberAccessNode member:
                foreach (ReferenceNode r in CollectReferences(member.Target))
                {
                    yield return r;
                }

                break;
            case IndexAccessNode index:
                foreach (ReferenceNode r in CollectReferences(index.Target))
                {
                    yield return r;
                }

                foreach (ReferenceNode r in CollectReferences(index.Index))
                {
                    yield return r;
                }

                break;
            case UnaryNode unary:
                foreach (ReferenceNode r in CollectReferences(unary.Operand))
                {
                    yield return r;
                }

                break;
            case BinaryNode binary:
                foreach (ReferenceNode r in CollectReferences(binary.Left))
                {
                    yield return r;
                }

                foreach (ReferenceNode r in CollectReferences(binary.Right))
                {
                    yield return r;
                }

                break;
            case ListLiteralNode list:
                foreach (ExpressionNode item in list.Items)
                {
                    foreach (ReferenceNode r in CollectReferences(item))
                    {
                        yield return r;
                    }
                }

                break;
            case MapLiteralNode map:
                foreach (MapEntry entry in map.Entries)
                {
                    foreach (ReferenceNode r in CollectReferences(entry.Value))
                    {
                        yield return r;
                    }
                }

                break;
            case CallNode call:
                foreach (ExpressionNode arg in call.Arguments)
                {
                    foreach (ReferenceNode r in CollectReferences(arg))
                    {
                        yield return r;
                    }
                }

                break;
        }
    }
}
