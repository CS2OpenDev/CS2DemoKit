#region

using System.Globalization;
using CS2DemoKit.Analysis.RulesetsV2.Model;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2;

/// <summary>
///     Structural validation over an <b>expanded</b>
///     <see cref="RulesetDoc" /> — pre-catalog, so it knows nothing about which views/facets/stats
///     exist (that is the resolver). It enforces the closed set of shape rules
///     not already caught while building the model:
///     <list type="bullet">
///         <item><c>keep:</c> is legal only under <c>capture:</c>.</item>
///         <item>param types are consistent with their default and <c>min</c>/<c>max</c> bounds.</item>
///         <item>ids are unique across the shared stat/highlight/param/define namespace (post-expansion).</item>
///         <item><c>title:</c> templates have balanced, non-empty <c>{}</c> holes.</item>
///         <item>the reserved <c>actor:</c> key carries only the keyword <c>any</c>.</item>
///     </list>
///     The kind discriminator and <c>match:</c> unary-test parsing are validated while mapping (the
///     model cannot be built otherwise); their diagnostics flow through the same pipeline.
/// </summary>
public static class RulesetStructuralValidator
{
    /// <summary>The legal <c>bucket:</c> <c>reduce:</c> reducer names (C8 named reducers).</summary>
    private static readonly HashSet<string> _bucketReducers =
        new(StringComparer.Ordinal)
        {
            "sum",
            "count",
            "min",
            "max",
            "last",
            "first"
        };

    /// <summary>Validates an expanded ruleset document, returning every structural diagnostic in document order.</summary>
    /// <param name="doc">The expanded ruleset document.</param>
    /// <returns>The diagnostics; empty when the document is structurally sound.</returns>
    public static IReadOnlyList<RulesetDiagnostic> Validate(RulesetDoc doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        List<RulesetDiagnostic> diagnostics = [];

        ValidateParams(doc, diagnostics);
        ValidateStats(doc, diagnostics);
        ValidateHighlights(doc, diagnostics);
        ValidateDuplicateIds(doc, diagnostics);
        ValidateExports(doc, diagnostics);

        return diagnostics;
    }

    /// <summary>
    ///     Lints the <c>exports:</c> subset: every id it names must be a
    ///     declared stat or highlight of this document. An absent list (<c>Exports == null</c>) means
    ///     export-all and is never flagged.
    /// </summary>
    private static void ValidateExports(RulesetDoc doc, List<RulesetDiagnostic> diagnostics)
    {
        if (doc.Exports is not { } exports)
        {
            return;
        }

        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (StatDef stat in doc.Stats)
        {
            declared.Add(stat.Id);
        }

        foreach (HighlightDef highlight in doc.Highlights)
        {
            declared.Add(highlight.Id);
        }

        foreach (string exported in exports)
        {
            if (!declared.Contains(exported))
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownExport,
                    $"exports: names '{exported}', which is not a declared stat or highlight", doc.Position));
            }
        }
    }

    // ── keep: / actor: (stats) ─────────────────────────────────────────────────

    private static void ValidateStats(RulesetDoc doc, List<RulesetDiagnostic> diagnostics)
    {
        foreach (StatDef stat in doc.Stats)
        {
            if (stat.Keep is not null && stat.Kind is not (StatKind.Capture or StatKind.None))
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.KeepNotOnCapture,
                    $"stat '{stat.Id}' has 'keep:' but is a '{KindName(stat.Kind)}' — keep: is legal only under capture:",
                    stat.Position));
            }

            ValidateTally(stat, diagnostics);
            ValidateStreak(stat, diagnostics);
            ValidateBurst(stat, diagnostics);
            ValidateBucket(stat, diagnostics);
            ValidateRate(stat, diagnostics);
            ValidateActor(stat.Trigger, stat.Id, diagnostics);
            ValidateActor(stat.OffTrigger, stat.Id, diagnostics);
        }

        foreach (DefineDef define in doc.Defines)
        {
            if (define.Body is TriggerDefineBody triggerBody)
            {
                ValidateActor(triggerBody.Trigger, define.Name, diagnostics);
            }
            else if (define.Body is MapDefineBody { ValueType: null } mixedMap)
            {
                // A map define whose values were neither all-number nor all-string (spec §3.4).
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.MixedMapDefine,
                    $"define '{define.Name}' is a map with mixed value types — all values must be numbers or all strings",
                    mixedMap.Pos));
            }
        }
    }

    /// <summary>
    ///     Validates the <c>tally:</c> kind-args: <c>thresholds:</c> is legal only under <c>tally:</c>,
    ///     a <c>tally:</c> needs at least one threshold, and its kind value (the source stat) must be
    ///     present. Threshold <c>min</c>/<c>target</c> well-formedness is enforced while mapping.
    /// </summary>
    private static void ValidateTally(StatDef stat, List<RulesetDiagnostic> diagnostics)
    {
        if (stat.Thresholds is { Count: > 0 } && stat.Kind is not (StatKind.Tally or StatKind.None))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"stat '{stat.Id}' has 'thresholds:' but is a '{KindName(stat.Kind)}' — thresholds: is legal only under tally:",
                stat.Position));
        }

        if (stat.Kind != StatKind.Tally)
        {
            return;
        }

        if (stat.Thresholds is not { Count: > 0 })
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"tally '{stat.Id}' needs at least one entry under 'thresholds:' (each a {{ min, target }})",
                stat.Position));
        }

        if (string.IsNullOrWhiteSpace(stat.KindArg))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"tally '{stat.Id}' needs a source value: 'tally: <sibling-stat>' (the value the thresholds tally over)",
                stat.Position));
        }
    }

    /// <summary>
    ///     Validates the <c>streak:</c> kind-args: <c>window:</c>/<c>min_streak:</c> are legal only under
    ///     <c>streak:</c>, a <c>streak:</c> needs an event source (the kind value), and a present
    ///     <c>min_streak:</c> must be positive. Window text well-formedness (int or duration) is checked
    ///     in the resolver, where the tick rate is known.
    /// </summary>
    private static void ValidateStreak(StatDef stat, List<RulesetDiagnostic> diagnostics)
    {
        bool hasStreakArgs = stat.StreakWindow is not null || stat.StreakMinStreak is not null;
        if (hasStreakArgs && stat.Kind is not (StatKind.Streak or StatKind.Burst or StatKind.None))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"stat '{stat.Id}' has 'window:'/'min_streak:' but is a '{KindName(stat.Kind)}' — they are legal only under streak: or burst:",
                stat.Position));
        }

        if (stat.Kind != StatKind.Streak)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stat.KindArg))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"streak '{stat.Id}' needs an event source: 'streak: <view|raw.event>' (the events whose streaks are counted)",
                stat.Position));
        }

        if (stat.StreakMinStreak is { } min && min < 1)
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"streak '{stat.Id}' has min_streak {min} — it must be a positive length", stat.Position));
        }
    }

    /// <summary>
    ///     Structural checks for a <c>burst:</c> pulse (the windowed multi-kill kind): it needs an event
    ///     source and a <c>min_streak</c> of at least 2 (a burst is two-or-more events). The shared
    ///     <c>window:</c>/<c>min_streak:</c> "legal only under streak: or burst:" guard lives in
    ///     <see cref="ValidateStreak" />; the window value is folded/validated in the resolver.
    /// </summary>
    private static void ValidateBurst(StatDef stat, List<RulesetDiagnostic> diagnostics)
    {
        if (stat.Kind != StatKind.Burst)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stat.KindArg))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"burst '{stat.Id}' needs an event source: 'burst: <view|raw.event>' (the events that form the burst)",
                stat.Position));
        }

        if (stat.StreakMinStreak is { } min && min < 2)
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"burst '{stat.Id}' has min_streak {min} — a burst is at least 2 events", stat.Position));
        }
    }

    /// <summary>
    ///     Validates the <c>bucket:</c> kind-args: <c>key:</c> / <c>value:</c> are legal only under
    ///     <c>bucket:</c>, a <c>bucket:</c> needs an event source (the kind value) and a <c>key:</c>,
    ///     an optional <c>value:</c> must be non-empty when present (the single-value SUM reducer; its
    ///     absence is a plain count bucket), and the stat must be match-scoped (its keyed node is
    ///     snapshot-excluded — a <c>per: round</c> sample would read end-of-game state, exactly as v1
    ///     rejects a round-scoped keyed counter).
    /// </summary>
    private static void ValidateBucket(StatDef stat, List<RulesetDiagnostic> diagnostics)
    {
        bool hasKey = stat.BucketKey is not null || stat.BucketKeys is { Count: > 0 };
        if (hasKey && stat.Kind is not (StatKind.Bucket or StatKind.None))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"stat '{stat.Id}' has 'key:' but is a '{KindName(stat.Kind)}' — key: is legal only under bucket:",
                stat.Position));
        }

        if (stat.BucketValue is not null && stat.Kind is not (StatKind.Bucket or StatKind.None))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"stat '{stat.Id}' has 'value:' but is a '{KindName(stat.Kind)}' — value: is legal only under bucket: "
                + "(the per-bucket reducer's amount)",
                stat.Position));
        }

        if (stat.BucketReduce is not null && stat.Kind is not (StatKind.Bucket or StatKind.None))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"stat '{stat.Id}' has 'reduce:' but is a '{KindName(stat.Kind)}' — reduce: is legal only under bucket:",
                stat.Position));
        }

        if (stat.Kind != StatKind.Bucket)
        {
            return;
        }

        if (stat.BucketReduce is not null && !_bucketReducers.Contains(stat.BucketReduce))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' has an unknown 'reduce:' value '{stat.BucketReduce}' — expected one of "
                + "sum, count, min, max, last, first",
                stat.Position));
        }

        if (string.IsNullOrWhiteSpace(stat.KindArg))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' needs an event source: 'bucket: <view|raw.event>' (the events keyed into buckets)",
                stat.Position));
        }

        if (stat.BucketKey is not null && stat.BucketKeys is { Count: > 0 })
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' has 'key:' as both a scalar and a list — author it as one or the other",
                stat.Position));
        }
        else if (string.IsNullOrWhiteSpace(stat.BucketKey) && stat.BucketKeys is not { Count: > 0 })
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' needs a 'key:' expression selecting the per-event bucket (e.g. key: event.Weapon "
                + "or a list [event.Weapon, ...] for a composite key)",
                stat.Position));
        }
        else if (stat.BucketKeys is { Count: > 0 } parts && parts.Any(string.IsNullOrWhiteSpace))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' has an empty part in its composite 'key:' list — each part must be a non-empty expression",
                stat.Position));
        }

        // value: is optional (its absence = a plain count bucket); when written it must be non-empty —
        // an empty/whitespace value: is a per-bucket SUM reducer with nothing to sum.
        if (stat.BucketValue is not null && string.IsNullOrWhiteSpace(stat.BucketValue))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' has an empty 'value:' — omit it for a count bucket, or give it a numeric "
                + "expression to sum (e.g. value: enrich.hurt.capped_damage)",
                stat.Position));
        }

        if (stat.Per == PerScope.Round)
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"bucket '{stat.Id}' is 'per: round', but a keyed bucket is match-scoped (sampled per-game) — use per: match",
                stat.Position));
        }
    }

    /// <summary>
    ///     Validates the <c>rate:</c> kind-args (G3 per-key ratios): a <c>rate:</c> needs both a
    ///     <c>of:</c> (numerator bucket) and a <c>per:</c> (denominator bucket) sub-key, and — like a
    ///     bucket — it must be match-scoped (its <c>KeyedRatioNode</c> reads two snapshot-excluded keyed
    ///     nodes sampled per-game; a <c>per: round</c> sample would read end-of-game state). The
    ///     <c>of:</c>/<c>per:</c> are nested under <c>rate:</c>, so they can never appear outside it (a
    ///     top-level <c>of:</c>/<c>per:</c> stat key is an unknown key, caught while mapping). The
    ///     resolver enforces that both refs are numeric sibling buckets keying on identical <c>key:</c>
    ///     parts.
    /// </summary>
    private static void ValidateRate(StatDef stat, List<RulesetDiagnostic> diagnostics)
    {
        if (stat.Kind != StatKind.Rate)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(stat.RateOf) || string.IsNullOrWhiteSpace(stat.RatePer))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"rate '{stat.Id}' needs both 'of:' (numerator bucket) and 'per:' (denominator bucket) — "
                + "e.g. rate: {{ of: hs_by_weapon, per: kills_by_weapon }}",
                stat.Position));
        }

        if (stat.Per == PerScope.Round)
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKindArgs,
                $"rate '{stat.Id}' is 'per: round', but a per-key rate is match-scoped (it reads two "
                + "match-scoped buckets sampled per-game) — use per: match",
                stat.Position));
        }
    }

    private static void ValidateActor(TriggerDef? trigger, string ownerId, List<RulesetDiagnostic> diagnostics)
    {
        if (trigger?.Actor is { } actor && actor != "any")
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadActor,
                $"'{ownerId}' has 'actor: {actor}' — the only legal actor value is 'any'",
                trigger.Position));
        }
    }

    // ── params ─────────────────────────────────────────────────────────────────

    private static void ValidateParams(RulesetDoc doc, List<RulesetDiagnostic> diagnostics)
    {
        foreach (ParamDef param in doc.Params)
        {
            if (param.Type == ParamType.None)
            {
                continue; // the mapper already reported the bad type; nothing else is checkable
            }

            bool numeric = param.Type is ParamType.Int or ParamType.Float or ParamType.Duration;
            if (!numeric && (param.Min is not null || param.Max is not null))
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
                    $"param '{param.Name}' is '{TypeName(param.Type)}' — min/max apply only to numeric or duration params",
                    param.Position));
                continue;
            }

            if (param.Min is { } min && param.Max is { } max && min > max)
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
                    $"param '{param.Name}' has min {Fmt(min)} greater than max {Fmt(max)}", param.Position));
            }

            ValidateParamDefault(param, diagnostics);
        }
    }

    private static void ValidateParamDefault(ParamDef param, List<RulesetDiagnostic> diagnostics)
    {
        if (param.Default is null)
        {
            return;
        }

        bool wellTyped = param.Type switch
        {
            ParamType.Int => param.Default is long,
            ParamType.Float => param.Default is double or long,
            ParamType.Bool => param.Default is bool,
            _ => true // string / duration keep their raw text
        };

        if (!wellTyped)
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
                $"param '{param.Name}' default '{param.Default}' is not a valid '{TypeName(param.Type)}'",
                param.Position));
            return;
        }

        if (param.Type is ParamType.Int or ParamType.Float && TryToDouble(param.Default, out double value))
        {
            if (param.Min is { } min && value < min)
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
                    $"param '{param.Name}' default {Fmt(value)} is below its min {Fmt(min)}", param.Position));
            }
            else if (param.Max is { } max && value > max)
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
                    $"param '{param.Name}' default {Fmt(value)} is above its max {Fmt(max)}", param.Position));
            }
        }
    }

    // ── title templates (highlights) ───────────────────────────────────────────

    private static void ValidateHighlights(RulesetDoc doc, List<RulesetDiagnostic> diagnostics)
    {
        foreach (HighlightDef highlight in doc.Highlights)
        {
            if (!IsWellFormedTemplate(highlight.Title))
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadTitleTemplate,
                    $"highlight '{highlight.Id}' has a malformed title template — every '{{' must open a non-empty, non-nested hole closed by a matching '}}'",
                    highlight.Position));
            }

            if (highlight.Score is { } score and (< 0 or > 100))
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
                    $"highlight '{highlight.Id}' has score {score} — it must be between 0 and 100",
                    highlight.Position));
            }
        }
    }

    /// <summary>True when every <c>{</c> opens a non-nested, non-empty hole closed by a matching <c>}</c>.</summary>
    private static bool IsWellFormedTemplate(string template)
    {
        bool inHole = false;
        int holeContentLength = 0;
        foreach (char c in template)
        {
            switch (c)
            {
                case '{' when inHole:
                    return false; // nested '{'
                case '{':
                    inHole = true;
                    holeContentLength = 0;
                    break;
                case '}' when !inHole:
                    return false; // stray '}'
                case '}':
                    if (holeContentLength == 0)
                    {
                        return false; // empty hole
                    }

                    inHole = false;
                    break;
                default:
                    if (inHole && !char.IsWhiteSpace(c))
                    {
                        holeContentLength++;
                    }

                    break;
            }
        }

        return !inHole; // no unclosed '{'
    }

    // ── duplicate ids (shared namespace, post-expansion) ───────────────────────

    private static void ValidateDuplicateIds(RulesetDoc doc, List<RulesetDiagnostic> diagnostics)
    {
        Dictionary<string, string> firstSeen = new(StringComparer.Ordinal);

        foreach ((string id, string kind, SourcePosition pos) in EnumerateIds(doc))
        {
            if (firstSeen.TryGetValue(id, out string? firstKind))
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.DuplicateId,
                    $"duplicate id '{id}' ({kind}) — it is already defined as a {firstKind} in this ruleset's shared id namespace",
                    pos));
            }
            else
            {
                firstSeen[id] = kind;
            }
        }
    }

    private static IEnumerable<(string Id, string Kind, SourcePosition Position)> EnumerateIds(RulesetDoc doc)
    {
        foreach (ParamDef p in doc.Params)
        {
            yield return (p.Name, "param", p.Position);
        }

        foreach (DefineDef d in doc.Defines)
        {
            yield return (d.Name, "define", d.Position);
        }

        foreach (StatDef s in doc.Stats)
        {
            yield return (s.Id, "stat", s.Position);
        }

        foreach (HighlightDef h in doc.Highlights)
        {
            yield return (h.Id, "highlight", h.Position);
        }
    }

    // ── formatting helpers ─────────────────────────────────────────────────────

    private static bool TryToDouble(object value, out double result)
    {
        switch (value)
        {
            case long l:
                result = l;
                return true;
            case double d:
                result = d;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static string Fmt(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string KindName(StatKind kind) => kind.ToString().ToLowerInvariant();

    private static string TypeName(ParamType type) => type.ToString().ToLowerInvariant();
}
