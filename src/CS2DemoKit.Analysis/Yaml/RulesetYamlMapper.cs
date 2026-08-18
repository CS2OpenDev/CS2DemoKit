#region

using System.Globalization;
using CS2DemoKit.Analysis.RulesetsV2;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using YamlDotNet.RepresentationModel;

#endregion

namespace CS2DemoKit.Analysis.Yaml;

/// <summary>
///     Maps a v2 <c>ruleset:</c> YAML document (a <see cref="YamlMappingNode" /> from the
///     representation model, so every node carries its source position) into the
///     <see cref="RulesetDoc" /> document model. Mapping is structural only: expression slots
///     (<c>where:</c>, <c>when:</c>, <c>compute:</c>, …) are carried as raw text for the resolver;
///     only <c>match:</c> values are parsed here (into <see cref="UnaryTest" />) and
///     enum-valued keys are validated. Every problem becomes a <see cref="RulesetDiagnostic" /> —
///     the mapper never throws for content and always returns a best-effort document so all errors
///     surface in one pass.
/// </summary>
public static class RulesetYamlMapper
{
    private static readonly HashSet<string> _knownTopKeys =
    [
        "ruleset", "title", "summary", "for", "enabled", "use", "exports", "params", "define", "stats", "highlights",
        "show", "catalog_version", "min_app_version"
    ];

    private static readonly HashSet<string> _statKindKeys =
        ["flag", "count", "sum", "capture", "compute", "tally", "streak", "bucket", "rate", "burst"];

    private static readonly HashSet<string> _knownStatKeys =
    [
        "flag", "count", "sum", "capture", "compute", "tally", "streak", "bucket", "rate", "burst",
        "per", "keep", "on", "match", "where", "while", "off", "label", "format", "for_each",
        "thresholds", "window", "min_streak", "key", "value", "reduce"
    ];

    /// <summary>
    ///     Maps a v2 ruleset root mapping node into a <see cref="RulesetDoc" />.
    /// </summary>
    /// <param name="root">The document's root mapping node (its <c>ruleset:</c> key selected v2 dispatch).</param>
    /// <param name="file">The absolute source path, or <c>null</c> for in-memory YAML (positions carry it).</param>
    /// <returns>The mapped document plus every diagnostic.</returns>
    public static MapResult Map(YamlMappingNode root, string? file)
    {
        ArgumentNullException.ThrowIfNull(root);
        List<RulesetDiagnostic> diagnostics = [];

        string? id = null;
        string? title = null;
        string? summary = null;
        RulesetScope forScope = RulesetScope.Match;
        bool enabled = true;
        List<string> use = [];
        List<string>? exports = null;
        List<ParamDef> parameters = [];
        List<DefineDef> defines = [];
        List<StatDef> stats = [];
        List<HighlightDef> highlights = [];
        ShowDef? show = null;
        string? catalogVersion = null;
        string? minAppVersion = null;

        foreach ((YamlNode keyNode, YamlNode valueNode) in root.Children)
        {
            string? key = ScalarText(keyNode);
            if (key is null)
            {
                continue;
            }

            switch (key)
            {
                case "ruleset":
                    id = ScalarText(valueNode);
                    break;
                case "title":
                    title = ScalarText(valueNode);
                    break;
                case "summary":
                    summary = ScalarText(valueNode);
                    break;
                case "for":
                    forScope = MapForScope(valueNode, file, diagnostics);
                    break;
                case "enabled":
                    enabled = MapBool(valueNode, file, diagnostics) ?? true;
                    break;
                case "use":
                    use = MapStringList(valueNode);
                    break;
                case "exports":
                    exports = MapStringList(valueNode);
                    break;
                case "params":
                    parameters = MapParams(valueNode, file, diagnostics);
                    break;
                case "define":
                    defines = MapDefines(valueNode, file, diagnostics);
                    break;
                case "stats":
                    stats = MapStats(valueNode, file, diagnostics);
                    break;
                case "highlights":
                    highlights = MapHighlights(valueNode, file, diagnostics);
                    break;
                case "show":
                    show = MapShow(valueNode, file, diagnostics);
                    break;
                case "catalog_version":
                    catalogVersion = ScalarText(valueNode);
                    break;
                case "min_app_version":
                    minAppVersion = ScalarText(valueNode);
                    break;
                default:
                    diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                        $"unknown top-level key '{key}'", Pos(keyNode, file)));
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.Missing,
                "ruleset is missing a non-empty 'ruleset:' id", Pos(root, file)));
            return new MapResult(null, diagnostics);
        }

        RulesetDoc doc = new(id, title, summary, forScope, enabled, use, exports, parameters, defines, stats,
            highlights, show, catalogVersion, minAppVersion, Pos(root, file));
        return new MapResult(doc, diagnostics);
    }

    // ── Params ───────────────────────────────────────────────────────────────

    private static List<ParamDef> MapParams(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<ParamDef> result = [];
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("params:", "a map of name → { type, default, min, max }", node, file));
            return result;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? name = ScalarText(keyNode);
            if (name is null)
            {
                continue;
            }

            SourcePosition pos = Pos(keyNode, file);
            if (valueNode is not YamlMappingNode body)
            {
                diagnostics.Add(WrongShape($"param '{name}'", "a map of { type, default, min, max }", valueNode, file));
                continue;
            }

            ParamType type = ParamType.None;
            object? defaultValue = null;
            double? min = null;
            double? max = null;

            foreach ((YamlNode pk, YamlNode pv) in body.Children)
            {
                switch (ScalarText(pk))
                {
                    case "type":
                        type = MapParamType(pv, file, diagnostics);
                        break;
                    case "default":
                        defaultValue = ScalarText(pv);
                        break;
                    case "min":
                        min = MapDouble(pv, "min", file, diagnostics);
                        break;
                    case "max":
                        max = MapDouble(pv, "max", file, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                            $"unknown key '{ScalarText(pk)}' in param '{name}'", Pos(pk, file)));
                        break;
                }
            }

            result.Add(new ParamDef(name, type, CoerceDefault(defaultValue, type), min, max, pos));
        }

        return result;
    }

    private static object? CoerceDefault(object? raw, ParamType type)
    {
        if (raw is not string s)
        {
            return raw;
        }

        return type switch
        {
            ParamType.Int => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i) ? i : raw,
            ParamType.Float => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                ? d
                : raw,
            ParamType.Bool => bool.TryParse(s, out bool b) ? b : raw,
            _ => raw // string / duration keep their raw text
        };
    }

    // ── Defines ──────────────────────────────────────────────────────────────

    private static List<DefineDef> MapDefines(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<DefineDef> result = [];
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("define:", "a map of name → (list | trigger | expression)", node, file));
            return result;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? name = ScalarText(keyNode);
            if (name is null)
            {
                continue;
            }

            SourcePosition pos = Pos(keyNode, file);
            DefineBody body = valueNode switch
            {
                YamlSequenceNode seq => new ListDefineBody(SequenceTexts(seq), Pos(seq, file)),
                // A mapping with `on:` is a trigger; any other mapping is a string-keyed lookup
                // map. A trigger always names a source via `on:`, so its presence disambiguates.
                YamlMappingNode m => IsTriggerMapping(m)
                    ? new TriggerDefineBody(MapTrigger(m, file, diagnostics), Pos(m, file))
                    : MapMapDefine(m, file),
                _ => new ExpressionDefineBody(ScalarText(valueNode) ?? "", Pos(valueNode, file))
            };
            result.Add(new DefineDef(name, body, pos));
        }

        return result;
    }

    /// <summary>A trigger mapping is any mapping carrying an <c>on:</c> source key; everything else is a map define.</summary>
    private static bool IsTriggerMapping(YamlMappingNode map) =>
        map.Children.Keys.Any(k => ScalarText(k) == "on");

    /// <summary>
    ///     Maps a <c>define:</c> map body — a string-keyed lookup table (<c>{ak47: rifle, awp: sniper}</c>).
    ///     Classifies the uniform value type (all-number vs all-string); a mixed table sets
    ///     <see cref="MapValueType" /> to <c>null</c>, which <c>RulesetStructuralValidator</c> reports.
    /// </summary>
    private static MapDefineBody MapMapDefine(YamlMappingNode map, string? file)
    {
        List<MapDefineEntry> entries = [];
        bool anyNumber = false;
        bool anyNonNumber = false;
        foreach ((YamlNode k, YamlNode v) in map.Children)
        {
            string? key = ScalarText(k);
            string value = ScalarText(v) ?? "";
            if (key is null)
            {
                continue;
            }

            bool isNumber = double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
            anyNumber |= isNumber;
            anyNonNumber |= !isNumber;
            entries.Add(new MapDefineEntry(key, value));
        }

        MapValueType? valueType = anyNumber && anyNonNumber
            ? null // mixed → structural error
            : anyNumber
                ? MapValueType.Number
                : MapValueType.String;
        return new MapDefineBody(entries, valueType, Pos(map, file));
    }

    // ── Stats ────────────────────────────────────────────────────────────────

    private static List<StatDef> MapStats(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<StatDef> result = [];
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("stats:", "a map of id → stat", node, file));
            return result;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? statId = ScalarText(keyNode);
            if (statId is null)
            {
                continue;
            }

            SourcePosition pos = Pos(keyNode, file);
            if (valueNode is not YamlMappingNode body)
            {
                diagnostics.Add(WrongShape($"stat '{statId}'", "a map with a kind key", valueNode, file));
                continue;
            }

            result.Add(MapOneStat(statId, body, pos, file, diagnostics));
        }

        return result;
    }

    private static StatDef MapOneStat(
        string statId, YamlMappingNode body, SourcePosition pos, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<string> kindKeys = [];
        string? kindArg = null;
        PerScope per = PerScope.None;
        KeepMode? keep = null;
        string? label = null;
        TriggerRef? on = null;
        List<MatchBinding> match = [];
        string? actor = null;
        string? where = null;
        string? whileRef = null;
        TriggerDef? offTrigger = null;
        List<ForEachAxis>? forEach = null;
        List<TallyThreshold>? thresholds = null;
        string? streakWindow = null;
        int? streakMinStreak = null;
        string? bucketKey = null;
        IReadOnlyList<string>? bucketKeys = null;
        string? bucketValue = null;
        string? bucketReduce = null;
        bool live = false;
        string? rateOf = null;
        string? ratePer = null;
        string? format = null;

        foreach ((YamlNode k, YamlNode v) in body.Children)
        {
            string? key = ScalarText(k);
            if (key is null)
            {
                continue;
            }

            if (_statKindKeys.Contains(key))
            {
                kindKeys.Add(key);
                kindArg = KindArgText(v, statId, file, diagnostics);

                // compute: { value: <expr>, live: true } — the opt-in live cadence.
                // The scalar form compute: "<expr>" leaves live == false (byte-identical to before live existed).
                // Only compute reads live:; the mapper stays shape-dumb (any other kind's mapping ignores it).
                if (key == "compute" && v is YamlMappingNode computeMap)
                {
                    live = ReadBoolChild(computeMap, "live");
                }

                // rate: { of: <bucket>, per: <bucket> } — the G3 per-key ratio's numerator/denominator
                // sibling-bucket ids. The nested of:/per: are read here (not the top-level switch), so
                // the denominator per: never collides with the stat-level per: reset scope. The mapper
                // stays shape-dumb (presence + bucket-typing is the resolver/structural validator's job).
                if (key == "rate" && v is YamlMappingNode rateMap)
                {
                    rateOf = ReadScalarChild(rateMap, "of");
                    ratePer = ReadScalarChild(rateMap, "per");
                }

                continue;
            }

            switch (key)
            {
                case "thresholds":
                    thresholds = MapThresholds(v, statId, file, diagnostics);
                    break;
                case "window":
                    streakWindow = ScalarText(v);
                    break;
                case "min_streak":
                    streakMinStreak = (int?)MapLong(v, "min_streak", file, diagnostics);
                    break;
                case "key":
                    // key: is either a single scalar expression (the common case) or a YAML list of
                    // expressions → a composite/tuple key (C8). A list element that is not a scalar maps
                    // to an empty part so the structural validator can flag it.
                    if (v is YamlSequenceNode keySeq)
                    {
                        bucketKeys = keySeq.Children.Select(c => ScalarText(c) ?? string.Empty).ToList();
                    }
                    else
                    {
                        bucketKey = ScalarText(v);
                    }

                    break;
                case "value":
                    bucketValue = ScalarText(v);
                    break;
                case "reduce":
                    bucketReduce = ScalarText(v);
                    break;
                case "per":
                    per = MapPerScope(v, file, diagnostics);
                    break;
                case "keep":
                    keep = MapKeep(v, file, diagnostics);
                    break;
                case "label":
                    label = ScalarText(v);
                    break;
                case "format":
                    // A compute:'s display format string (.NET numeric format, e.g. F2). A pure display
                    // attribute (like label:) — carried through to the ComputedStatNode, never hashed.
                    format = ScalarText(v);
                    break;
                case "on":
                    on = MapTriggerRef(v, file);
                    break;
                case "match":
                    match = MapMatch(v, ref actor, file, diagnostics);
                    break;
                case "where":
                    where = ScalarText(v);
                    break;
                case "while":
                    whileRef = ScalarText(v);
                    break;
                case "off":
                    offTrigger = v is YamlMappingNode offMap
                        ? MapTrigger(offMap, file, diagnostics)
                        : new TriggerDef(MapTriggerRef(v, file), [], null, null, null, Pos(v, file));
                    break;
                case "for_each":
                    forEach = MapForEach(v, file, diagnostics);
                    break;
                default:
                    diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                        $"unknown key '{key}' in stat '{statId}'", Pos(k, file)));
                    break;
            }
        }

        StatKind kind = ResolveKind(statId, kindKeys, pos, diagnostics);
        if (per == PerScope.None)
        {
            // A bucket (and a rate over buckets) is inherently match-scoped — its keyed node is
            // snapshot-excluded, sampled per-game; every other kind defaults to per-round. An explicit
            // per: round on a bucket/rate is rejected structurally.
            per = kind is StatKind.Bucket or StatKind.Rate ? PerScope.Match : PerScope.Round;
        }

        bool hasTrigger = on is not null || match.Count > 0 || where is not null || whileRef is not null
                          || actor is not null;
        TriggerDef? trigger = hasTrigger
            ? new TriggerDef(on, match, actor, where, whileRef, pos)
            : null;

        return new StatDef(statId, kind, kindArg, per, keep, trigger, offTrigger, forEach, label, pos, thresholds,
            streakWindow, streakMinStreak, bucketKey, bucketValue, bucketKeys, bucketReduce, live, rateOf, ratePer,
            format);
    }

    /// <summary>
    ///     Reads a scalar child value from a mapping node (e.g. <c>of:</c> / <c>per:</c> under a
    ///     <c>rate: { … }</c>). Absent or non-scalar ⇒ <c>null</c> — the mapper stays lenient (the
    ///     resolver / structural validator owns presence + typing).
    /// </summary>
    private static string? ReadScalarChild(YamlMappingNode map, string childKey)
    {
        foreach ((YamlNode k, YamlNode v) in map.Children)
        {
            if (ScalarText(k) == childKey)
            {
                return ScalarText(v);
            }
        }

        return null;
    }

    /// <summary>
    ///     Reads a boolean child scalar from a mapping node (e.g. <c>live:</c> under a
    ///     <c>compute: { … }</c>). Absent, non-scalar, or non-<c>true</c> ⇒ <c>false</c> — the mapper
    ///     stays deliberately lenient (the resolver owns semantic validation).
    /// </summary>
    private static bool ReadBoolChild(YamlMappingNode map, string childKey)
    {
        foreach ((YamlNode k, YamlNode v) in map.Children)
        {
            if (ScalarText(k) == childKey)
            {
                return bool.TryParse(ScalarText(v), out bool parsed) && parsed;
            }
        }

        return false;
    }

    /// <summary>Maps a <c>tally:</c> stat's <c>thresholds:</c> sequence of <c>{ min, target }</c> entries.</summary>
    private static List<TallyThreshold>? MapThresholds(
        YamlNode node, string statId, string? file, List<RulesetDiagnostic> diagnostics)
    {
        if (node is not YamlSequenceNode seq)
        {
            diagnostics.Add(WrongShape($"thresholds: in stat '{statId}'", "a list of { min, target }", node, file));
            return null;
        }

        List<TallyThreshold> result = [];
        foreach (YamlNode entry in seq)
        {
            if (entry is not YamlMappingNode m)
            {
                diagnostics.Add(WrongShape($"a threshold in stat '{statId}'", "a map with 'min' and 'target'", entry,
                    file));
                continue;
            }

            TallyMin? min = null;
            string? target = null;
            foreach ((YamlNode k, YamlNode v) in m.Children)
            {
                switch (ScalarText(k))
                {
                    case "min":
                        min = MapTallyMin(v);
                        break;
                    case "target":
                        target = ScalarText(v);
                        break;
                    default:
                        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                            $"unknown key '{ScalarText(k)}' in a threshold of stat '{statId}'", Pos(k, file)));
                        break;
                }
            }

            if (min is null || string.IsNullOrWhiteSpace(target))
            {
                diagnostics.Add(WrongShape($"a threshold in stat '{statId}'",
                    "a map with a 'min' (integer or params.<name>) and a non-empty 'target'", m, file));
                continue;
            }

            result.Add(new TallyThreshold(min, target, Pos(m, file)));
        }

        return result;
    }

    /// <summary>
    ///     Maps a tally threshold's <c>min:</c> scalar to a <see cref="TallyMin" />: an integer literal
    ///     (the authored-integer form) or, when the scalar is not an integer, a
    ///     <see cref="TallyMinParam" /> carrying the raw text (a <c>params.&lt;name&gt;</c> reference the
    ///     resolver binds to its literal int). The resolver validates the param is bound and int-typed;
    ///     the mapper stays deliberately shape-dumb. A structurally-absent scalar returns <c>null</c>
    ///     (caught by the threshold's shape check).
    /// </summary>
    private static TallyMin? MapTallyMin(YamlNode node)
    {
        string? text = ScalarText(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long literal)
            ? new TallyMinLiteral((int)literal)
            : new TallyMinParam(text.Trim());
    }

    private static StatKind ResolveKind(
        string statId, List<string> kindKeys, SourcePosition pos, List<RulesetDiagnostic> diagnostics)
    {
        switch (kindKeys.Count)
        {
            case 0:
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKind,
                    $"stat '{statId}' has no kind — declare exactly one of flag/count/sum/capture/compute/tally/streak/bucket/rate",
                    pos));
                return StatKind.None;
            case > 1:
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadKind,
                    $"stat '{statId}' declares multiple kinds ({string.Join(", ", kindKeys)}) — a stat is exactly one kind",
                    pos));
                return StatKind.None;
            default:
                return kindKeys[0] switch
                {
                    "flag" => StatKind.Flag,
                    "count" => StatKind.Count,
                    "sum" => StatKind.Sum,
                    "capture" => StatKind.Capture,
                    "compute" => StatKind.Compute,
                    "tally" => StatKind.Tally,
                    "streak" => StatKind.Streak,
                    "bucket" => StatKind.Bucket,
                    "rate" => StatKind.Rate,
                    "burst" => StatKind.Burst,
                    _ => StatKind.None
                };
        }
    }

    /// <summary>Renders a kind key's value as its primary argument text (a scalar, or a mapping's <c>when:</c> body).</summary>
    private static string? KindArgText(YamlNode value, string statId, string? file, List<RulesetDiagnostic> diagnostics)
    {
        if (value is YamlMappingNode map)
        {
            foreach ((YamlNode k, YamlNode v) in map.Children)
            {
                string? kt = ScalarText(k);
                if (kt == "when")
                {
                    return WhenText(v, $"when: in stat '{statId}'", file, diagnostics);
                }

                // compute: { value: <expr>, live: … } — the formula rides the value: key (the sibling
                // live: key is read separately by the caller). Byte-identical to the scalar compute:
                // "<expr>" form once value: is unwrapped.
                if (kt == "value")
                {
                    return ScalarText(v);
                }
            }

            return null;
        }

        return ScalarText(value);
    }

    /// <summary>
    ///     Renders a <c>when:</c> value as a single predicate string. A YAML scalar passes through
    ///     unchanged. A YAML sequence is the AND-conjunction of its items (condition source
    ///     lists): each item is parenthesized and joined with <c> and </c>, producing EXACTLY the
    ///     string a hand-written <c>(p1) and (p2) and …</c> would — so the list form is pure sugar over
    ///     the scalar <c>when:</c> path (identical canonical AST ⇒ identical identity hash ⇒ identical
    ///     lowering). A single-item list <c>[p]</c> collapses to the bare item <c>p</c> (no spurious
    ///     <c>and</c>, byte-identical to the scalar form). An empty list <c>[]</c> is a structural error
    ///     (a <c>when:</c> must constrain something) → <c>null</c> plus a <c>WrongShape</c> diagnostic.
    /// </summary>
    private static string? WhenText(YamlNode node, string what, string? file, List<RulesetDiagnostic> diagnostics)
    {
        if (node is not YamlSequenceNode seq)
        {
            return ScalarText(node);
        }

        List<string> items = SequenceTexts(seq);
        if (items.Count == 0)
        {
            diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.WrongShape,
                $"{what} must not be an empty list — a when: must constrain something", Pos(node, file)));
            return null;
        }

        return items.Count == 1
            ? items[0]
            : string.Join(" and ", items.Select(item => $"({item})"));
    }

    // ── Highlights ─────────────────────────────────────────────────────────────

    private static List<HighlightDef> MapHighlights(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<HighlightDef> result = [];
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("highlights:", "a map of id → highlight", node, file));
            return result;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? hlId = ScalarText(keyNode);
            if (hlId is null)
            {
                continue;
            }

            SourcePosition pos = Pos(keyNode, file);
            if (valueNode is not YamlMappingNode body)
            {
                diagnostics.Add(WrongShape($"highlight '{hlId}'", "a map with 'when' and 'title'", valueNode, file));
                continue;
            }

            string when = "";
            PerScope per = PerScope.None;
            string title = "";
            List<ForEachAxis>? forEach = null;
            int? score = null;
            string? kind = null;
            string? group = null;

            foreach ((YamlNode k, YamlNode v) in body.Children)
            {
                switch (ScalarText(k))
                {
                    case "when":
                        when = WhenText(v, $"when: in highlight '{hlId}'", file, diagnostics) ?? "";
                        break;
                    case "per":
                        per = MapPerScope(v, file, diagnostics);
                        break;
                    case "title":
                        title = ScalarText(v) ?? "";
                        break;
                    case "for_each":
                        forEach = MapForEach(v, file, diagnostics);
                        break;
                    case "score":
                        // Kept raw; the resolver applies the default and range-validates.
                        score = (int?)MapLong(v, $"score: in highlight '{hlId}'", file, diagnostics);
                        break;
                    case "kind":
                        // Kept raw text; the resolver maps it to HighlightKind and reports a bad value.
                        kind = ScalarText(v);
                        break;
                    case "group":
                        // Raw supersession-family tag; the resolver trims it. Surfacing keeps only the
                        // top-scored firing per player+round+group (tiered families → their top tier).
                        group = ScalarText(v);
                        break;
                    default:
                        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                            $"unknown key '{ScalarText(k)}' in highlight '{hlId}'", Pos(k, file)));
                        break;
                }
            }

            if (per == PerScope.None)
            {
                per = PerScope.Round; // highlights default to per-round rising edges
            }

            result.Add(new HighlightDef(hlId, when, per, title, forEach, score, kind, group, pos));
        }

        return result;
    }

    // ── Show ───────────────────────────────────────────────────────────────────

    private static ShowDef? MapShow(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("show:", "a map with 'scoreboard' and/or 'tables'", node, file));
            return null;
        }

        List<ScoreboardEntry> scoreboard = [];
        List<TableDef> tables = [];

        foreach ((YamlNode k, YamlNode v) in map.Children)
        {
            switch (ScalarText(k))
            {
                case "scoreboard":
                    scoreboard = MapScoreboard(v, file, diagnostics);
                    break;
                case "tables":
                    tables = MapTables(v, file, diagnostics);
                    break;
                default:
                    diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                        $"unknown key '{ScalarText(k)}' in show:", Pos(k, file)));
                    break;
            }
        }

        return new ShowDef(scoreboard, tables, Pos(node, file));
    }

    private static List<ScoreboardEntry> MapScoreboard(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<ScoreboardEntry> result = [];
        if (node is not YamlSequenceNode seq)
        {
            diagnostics.Add(WrongShape("scoreboard:", "a list of { stat, label, group, boards }", node, file));
            return result;
        }

        foreach (YamlNode entry in seq)
        {
            if (entry is not YamlMappingNode m)
            {
                diagnostics.Add(WrongShape("scoreboard entry", "a map with a 'stat' key", entry, file));
                continue;
            }

            string stat = "";
            string? label = null;
            string? group = null;
            List<string>? boards = null;
            ColumnValueFormat asFormat = ColumnValueFormat.None;
            foreach ((YamlNode k, YamlNode v) in m.Children)
            {
                switch (ScalarText(k))
                {
                    case "stat":
                        stat = ScalarText(v) ?? "";
                        break;
                    case "label":
                        label = ScalarText(v);
                        break;
                    case "group":
                        group = ScalarText(v);
                        break;
                    case "boards":
                        boards = MapStringList(v);
                        break;
                    case "as":
                        asFormat = MapColumnFormat(v, file, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                            $"unknown key '{ScalarText(k)}' in a scoreboard entry", Pos(k, file)));
                        break;
                }
            }

            result.Add(new ScoreboardEntry(stat, label, group, boards, asFormat, Pos(m, file)));
        }

        return result;
    }

    private static List<TableDef> MapTables(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<TableDef> result = [];
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("tables:", "a map of name → { per, columns }", node, file));
            return result;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? tableName = ScalarText(keyNode);
            if (tableName is null || valueNode is not YamlMappingNode body)
            {
                diagnostics.Add(WrongShape($"table '{tableName}'", "a map with 'per' and 'columns'", valueNode, file));
                continue;
            }

            string? tablePer = null;
            List<TableColumn> columns = [];
            foreach ((YamlNode k, YamlNode v) in body.Children)
            {
                switch (ScalarText(k))
                {
                    case "per":
                        tablePer = ScalarText(v);
                        break;
                    case "columns":
                        columns = MapTableColumns(v, file, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                            $"unknown key '{ScalarText(k)}' in table '{tableName}'", Pos(k, file)));
                        break;
                }
            }

            result.Add(new TableDef(tableName, tablePer, columns, Pos(keyNode, file)));
        }

        return result;
    }

    private static List<TableColumn> MapTableColumns(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<TableColumn> result = [];
        if (node is not YamlSequenceNode seq)
        {
            diagnostics.Add(WrongShape("columns:", "a list of { stat, label }", node, file));
            return result;
        }

        foreach (YamlNode entry in seq)
        {
            if (entry is not YamlMappingNode m)
            {
                diagnostics.Add(WrongShape("column", "a map with a 'stat' key", entry, file));
                continue;
            }

            string stat = "";
            string? label = null;
            ColumnValueFormat asFormat = ColumnValueFormat.None;
            foreach ((YamlNode k, YamlNode v) in m.Children)
            {
                switch (ScalarText(k))
                {
                    case "stat":
                        stat = ScalarText(v) ?? "";
                        break;
                    case "label":
                        label = ScalarText(v);
                        break;
                    case "as":
                        asFormat = MapColumnFormat(v, file, diagnostics);
                        break;
                    default:
                        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                            $"unknown key '{ScalarText(k)}' in a table column", Pos(k, file)));
                        break;
                }
            }

            result.Add(new TableColumn(stat, label, asFormat, Pos(m, file)));
        }

        return result;
    }

    // ── Triggers & matches ─────────────────────────────────────────────────────

    private static TriggerDef MapTrigger(YamlMappingNode map, string? file, List<RulesetDiagnostic> diagnostics)
    {
        TriggerRef? on = null;
        List<MatchBinding> match = [];
        string? actor = null;
        string? where = null;
        string? whileRef = null;

        foreach ((YamlNode k, YamlNode v) in map.Children)
        {
            switch (ScalarText(k))
            {
                case "on":
                    on = MapTriggerRef(v, file);
                    break;
                case "match":
                    match = MapMatch(v, ref actor, file, diagnostics);
                    break;
                case "where":
                    where = ScalarText(v);
                    break;
                case "while":
                    whileRef = ScalarText(v);
                    break;
                default:
                    diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.UnknownKey,
                        $"unknown key '{ScalarText(k)}' in a trigger", Pos(k, file)));
                    break;
            }
        }

        return new TriggerDef(on, match, actor, where, whileRef, Pos(map, file));
    }

    private static TriggerRef? MapTriggerRef(YamlNode node, string? file)
    {
        string? text = ScalarText(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        SourcePosition pos = Pos(node, file);
        if (text == "this")
        {
            return new TriggerRef(TriggerRefKind.This, "this", pos);
        }

        if (text.StartsWith("raw.", StringComparison.Ordinal))
        {
            return new TriggerRef(TriggerRefKind.Raw, text["raw.".Length..], pos);
        }

        if (text.StartsWith("net.", StringComparison.Ordinal))
        {
            return new TriggerRef(TriggerRefKind.Net, text["net.".Length..], pos);
        }

        return new TriggerRef(TriggerRefKind.ViewOrDefine, text, pos);
    }

    private static List<MatchBinding> MapMatch(
        YamlNode node, ref string? actor, string? file, List<RulesetDiagnostic> diagnostics)
    {
        List<MatchBinding> result = [];
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("match:", "a map of facet → test", node, file));
            return result;
        }

        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? facet = ScalarText(keyNode);
            if (facet is null)
            {
                continue;
            }

            SourcePosition pos = Pos(keyNode, file);
            if (facet == "actor")
            {
                actor = ScalarText(valueNode);
                continue;
            }

            string? testText = MatchValueText(valueNode);
            if (testText is null)
            {
                diagnostics.Add(WrongShape($"match value for '{facet}'", "a scalar test or a '[lo..hi]' range",
                    valueNode, file));
                continue;
            }

            UnaryTest? test = UnaryTestParser.Parse(testText, Pos(valueNode, file), diagnostics);
            if (test is not null)
            {
                result.Add(new MatchBinding(facet, test, pos));
            }
        }

        return result;
    }

    /// <summary>Renders a match value node as unary-test source text: a scalar directly, or a sequence as <c>[a, b, …]</c>.</summary>
    private static string? MatchValueText(YamlNode node) =>
        node switch
        {
            YamlScalarNode scalar => scalar.Value ?? "",
            YamlSequenceNode seq => "[" + string.Join(", ", SequenceTexts(seq)) + "]",
            _ => null
        };

    // ── for_each ───────────────────────────────────────────────────────────────

    private static List<ForEachAxis>? MapForEach(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        if (node is not YamlMappingNode map)
        {
            diagnostics.Add(WrongShape("for_each:", "a map of key → [values]", node, file));
            return null;
        }

        List<ForEachAxis> axes = [];
        foreach ((YamlNode keyNode, YamlNode valueNode) in map.Children)
        {
            string? axisKey = ScalarText(keyNode);
            if (axisKey is null)
            {
                continue;
            }

            SourcePosition pos = Pos(keyNode, file);
            if (valueNode is not YamlSequenceNode seq || seq.Children.Count == 0)
            {
                diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadForEach,
                    $"for_each axis '{axisKey}' must be a non-empty list of values", pos));
                continue;
            }

            axes.Add(new ForEachAxis(axisKey, SequenceTexts(seq), pos));
        }

        return axes.Count > 0 ? axes : null;
    }

    // ── Enum & scalar helpers ──────────────────────────────────────────────────

    private static RulesetScope MapForScope(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics) =>
        ScalarText(node) switch
        {
            "match" => RulesetScope.Match,
            "each_player" => RulesetScope.EachPlayer,
            _ => BadEnum(node, "for", "match | each_player", RulesetScope.Match, file, diagnostics)
        };

    private static PerScope MapPerScope(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics) =>
        ScalarText(node) switch
        {
            "round" => PerScope.Round,
            "match" => PerScope.Match,
            _ => BadEnum(node, "per", "round | match", PerScope.Round, file, diagnostics)
        };

    private static ColumnValueFormat MapColumnFormat(
        YamlNode node, string? file, List<RulesetDiagnostic> diagnostics) =>
        ScalarText(node) switch
        {
            "ticks" => ColumnValueFormat.Ticks,
            "seconds" => ColumnValueFormat.Seconds,
            "time" => ColumnValueFormat.Time,
            _ => BadEnum(node, "as", "ticks | seconds | time", ColumnValueFormat.None, file,
                diagnostics)
        };

    private static KeepMode? MapKeep(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics) =>
        ScalarText(node) switch
        {
            "first" => KeepMode.First,
            "last" => KeepMode.Last,
            "list" => KeepMode.List,
            "min" => KeepMode.Min,
            "max" => KeepMode.Max,
            _ => BadEnum(node, "keep", "first | last | list | min | max", KeepMode.None, file, diagnostics)
        };

    private static ParamType MapParamType(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics) =>
        ScalarText(node) switch
        {
            "int" => ParamType.Int,
            "float" => ParamType.Float,
            "bool" => ParamType.Bool,
            "string" => ParamType.String,
            "duration" => ParamType.Duration,
            _ => BadEnum(node, "type", "int | float | bool | string | duration", ParamType.None, file,
                diagnostics)
        };

    private static TEnum BadEnum<TEnum>(
        YamlNode node, string keyName, string allowed, TEnum fallback, string? file,
        List<RulesetDiagnostic> diagnostics)
        where TEnum : struct, Enum
    {
        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadEnum,
            $"'{keyName}: {ScalarText(node)}' is not valid — expected one of {allowed}", Pos(node, file)));
        return fallback;
    }

    private static bool? MapBool(YamlNode node, string? file, List<RulesetDiagnostic> diagnostics)
    {
        string? text = ScalarText(node);
        if (bool.TryParse(text, out bool b))
        {
            return b;
        }

        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.BadEnum,
            $"'enabled: {text}' is not valid — expected true or false", Pos(node, file)));
        return null;
    }

    private static double? MapDouble(
        YamlNode node, string keyName, string? file, List<RulesetDiagnostic> diagnostics)
    {
        string? text = ScalarText(node);
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
        {
            return d;
        }

        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
            $"'{keyName}: {text}' is not a number", Pos(node, file)));
        return null;
    }

    private static long? MapLong(
        YamlNode node, string keyName, string? file, List<RulesetDiagnostic> diagnostics)
    {
        string? text = ScalarText(node);
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
        {
            return l;
        }

        diagnostics.Add(new RulesetDiagnostic(RulesetDiagnosticCodes.ParamRange,
            $"'{keyName}: {text}' is not an integer", Pos(node, file)));
        return null;
    }

    private static List<string> MapStringList(YamlNode node) =>
        node is YamlSequenceNode seq ? SequenceTexts(seq) : [];

    private static List<string> SequenceTexts(YamlSequenceNode seq)
    {
        List<string> items = [];
        foreach (YamlNode child in seq)
        {
            string? text = ScalarText(child);
            if (text is not null)
            {
                items.Add(text);
            }
        }

        return items;
    }

    private static RulesetDiagnostic WrongShape(string what, string expected, YamlNode node, string? file) =>
        new(RulesetDiagnosticCodes.WrongShape, $"{what} must be {expected}", Pos(node, file));

    private static string? ScalarText(YamlNode node) => (node as YamlScalarNode)?.Value;

    private static SourcePosition Pos(YamlNode node, string? file) =>
        new(file, (int)node.Start.Line, (int)node.Start.Column);

    /// <summary>The result of mapping one v2 ruleset document.</summary>
    /// <param name="Doc">
    ///     The mapped ruleset (best-effort even when diagnostics were produced), or <c>null</c> when there was
    ///     no id.
    /// </param>
    /// <param name="Diagnostics">Every mapping diagnostic, in document order.</param>
    public sealed record MapResult(RulesetDoc? Doc, IReadOnlyList<RulesetDiagnostic> Diagnostics);
}
