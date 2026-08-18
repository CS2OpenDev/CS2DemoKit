#region

using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Rules;
using CS2DemoKit.Analysis.Rules.Ast;
using CS2DemoKit.Analysis.Rules.Checking;
using CS2DemoKit.Analysis.Rules.Hashing;
using CS2DemoKit.Analysis.Rules.Lexing;
using CS2DemoKit.Analysis.Rules.Normalization;
using CS2DemoKit.Analysis.Rules.Parsing;
using CS2DemoKit.Analysis.Rules.Scopes;
using CS2DemoKit.Analysis.RulesetsV2.Compile;
using CS2DemoKit.Analysis.RulesetsV2.Model;
using MatchBinding = CS2DemoKit.Analysis.RulesetsV2.Model.MatchBinding;

#endregion

namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     The resolve → canonicalize → check pipeline. It turns one
///     expanded, structurally-valid <see cref="RulesetDoc" /> into the <see cref="CheckedRuleset" />
///     IR the planner compiles to a graph: it resolves trigger sources (views, defines,
///     raw/net triggers), lowers <c>match:</c> bindings and composes the fixed canonical conjunction,
///     inlines defines and binds params to literals, folds durations at the context tick rate, and
///     type-checks every slot against the Catalog-backed scope environments. It performs
///     <b>
///         no
///         hashing and builds no graph
///     </b>
///     . Stat-reference cycle detection is a separate pass
///     (<see cref="StatReferenceCycleDetector" />) the <see cref="CheckedRulesetDraft" /> runs.
/// </summary>
public static class RulesetResolver
{
    /// <summary>Resolves and checks a ruleset document.</summary>
    /// <param name="doc">The expanded, structurally-valid document.</param>
    /// <param name="adapter">The Catalog scope-environment adapter.</param>
    /// <param name="context">The load-vs-build context (tick rate, profile, param values).</param>
    /// <param name="exports">The cross-ruleset export graph, or null for single-document resolution.</param>
    /// <returns>The checked ruleset IR, or resolution/checking diagnostics.</returns>
    public static RulesetResolveResult Resolve(RulesetDoc doc, CatalogScopeAdapter adapter, ResolveContext context,
        RulesetExportGraph? exports = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(context);
        return new Session(doc, adapter, context, exports).Run();
    }

    private sealed class Session
    {
        /// <summary>The v1 windowed-streak defaults (max tick gap / minimum length) a streak inherits when unspecified.</summary>
        private const int DefaultStreakWindow = 640;

        private const int DefaultStreakMinStreak = 2;

        /// <summary>The neutral ranking weight a highlight inherits when <c>score:</c> is unspecified.</summary>
        private const int DefaultHighlightScore = 50;
        private readonly CatalogScopeAdapter _adapter;
        private readonly List<RulesetCoverageDiagnostic> _coverage = [];
        private readonly ResolveContext _ctx;
        private readonly Dictionary<string, DefineDef> _definesByName;
        private readonly List<RulesetDiagnostic> _diagnostics = [];
        private readonly RulesetDoc _doc;
        private readonly RulesetExportGraph? _exports;
        private readonly bool _forEachPlayer;
        private readonly Dictionary<string, ParamDef> _paramDefsByName = new(StringComparer.Ordinal);

        private readonly Dictionary<string, ExpressionNode> _paramLiterals = new(StringComparer.Ordinal);
        private readonly IScopeSymbol? _paramsNamespace;
        private readonly Dictionary<string, CatalogProvider> _perPlayerProviderByMember;
        private readonly Dictionary<string, CatalogProvider> _providerByV2Name;
        private readonly RulesetId _rulesetId;
        private readonly List<IScopeSymbol> _siblingSymbols = [];
        private readonly Dictionary<string, StatDef> _statsById;
        private readonly List<IScopeSymbol> _usedRulesetRoots = [];

        private readonly Dictionary<string, RulesType> _valueTypes = new(StringComparer.Ordinal);

        private readonly Dictionary<string, CatalogView> _viewsByName;

        internal Session(RulesetDoc doc, CatalogScopeAdapter adapter, ResolveContext ctx, RulesetExportGraph? exports)
        {
            _doc = doc;
            _adapter = adapter;
            _ctx = ctx;
            _exports = exports;
            _rulesetId = new RulesetId(doc.Id, doc.For);
            _forEachPlayer = doc.For == RulesetScope.EachPlayer;

            _viewsByName = adapter.Catalog.Views.ToDictionary(v => v.Name, StringComparer.Ordinal);
            _definesByName = doc.Defines.ToDictionary(d => d.Name, StringComparer.Ordinal);
            _statsById = doc.Stats.ToDictionary(s => s.Id, StringComparer.Ordinal);
            _providerByV2Name = adapter.Catalog.Providers
                .Where(p => p.V2Name is not null)
                .ToDictionary(p => p.V2Name!, StringComparer.Ordinal);
            _perPlayerProviderByMember = adapter.Catalog.Providers
                .Where(p => string.Equals(p.Scope, "perPlayer", StringComparison.Ordinal) && p.V2Name is not null)
                .ToDictionary(p => LastSegment(p.V2Name!), StringComparer.Ordinal);

            foreach (ParamDef param in doc.Params)
            {
                _paramDefsByName[param.Name] = param; // last-wins; structural validation flags dup names
            }

            _paramsNamespace = BuildParamsNamespace();
        }

        internal RulesetResolveResult Run()
        {
            if (_doc.For == RulesetScope.None)
            {
                Report(ResolveDiagnosticCodes.MissingScope, "ruleset has no 'for:' scope", _doc.Position);
                return new RulesetResolveResult(null, _diagnostics);
            }

            CheckThisShadow();
            BuildUsedRulesetRoots();
            InferValueTypes();

            // Pass A: every non-rate stat, in document order. Pass B: rate: stats (G3), which reference
            // two sibling buckets — resolving them last means both buckets have already been checked (so
            // their resolved key-parts are available to the identical-key comparison), regardless of the
            // rate's declaration position. Rates land at the tail of the stats list; the planner defers
            // rate lowering anyway (it pulls already-built bucket nodes), so tail order is correct.
            List<CheckedStat> stats = [];
            Dictionary<string, CheckedStat> checkedById = new(StringComparer.Ordinal);
            List<StatDef> rateStats = [];
            foreach (StatDef stat in _doc.Stats)
            {
                if (stat.Kind == StatKind.Rate)
                {
                    rateStats.Add(stat);
                    continue;
                }

                CheckedStat? checkedStat = ResolveStat(stat);
                if (checkedStat is not null)
                {
                    stats.Add(checkedStat);
                    checkedById[stat.Id] = checkedStat;
                }
            }

            foreach (StatDef rate in rateStats)
            {
                CheckedStat? checkedRate = ResolveRate(rate, checkedById);
                if (checkedRate is not null)
                {
                    stats.Add(checkedRate);
                }
            }

            List<CheckedHighlight> highlights = [];
            foreach (HighlightDef highlight in _doc.Highlights)
            {
                CheckedHighlight? checkedHighlight = ResolveHighlight(highlight);
                if (checkedHighlight is not null)
                {
                    highlights.Add(checkedHighlight);
                }
            }

            if (_diagnostics.Count > 0)
            {
                return new RulesetResolveResult(null, _diagnostics);
            }

            CheckedRuleset ruleset = new(_rulesetId, _doc.Title, _doc.For, stats, highlights, _coverage, _doc.Show);
            return new RulesetResolveResult(ruleset, _diagnostics);
        }

        // ── Params ───────────────────────────────────────────────────────────────

        private ScopeSymbol? BuildParamsNamespace()
        {
            if (_doc.Params.Count == 0)
            {
                return null;
            }

            List<IScopeSymbol> members = [];
            foreach (ParamDef param in _doc.Params)
            {
                RulesType type = ParamRulesType(param.Type);
                members.Add(ScopeSymbol.Value(param.Name, type));
                if (_ctx.IsBuild)
                {
                    _paramLiterals[param.Name] = ParamLiteral(param);
                }
            }

            return ScopeSymbol.Namespace("params", [.. members]);
        }

        private ExpressionNode ParamLiteral(ParamDef param)
        {
            object? value = _ctx.ParamValues is not null && _ctx.ParamValues.TryGetValue(param.Name, out object? bound)
                ? bound
                : param.Default;

            return param.Type switch
            {
                ParamType.Int => new IntLiteralNode(ToLong(value)),
                ParamType.Float => new FloatLiteralNode(ToDouble(value)),
                ParamType.Bool => new BoolLiteralNode(value is true),
                ParamType.Duration => DurationParamLiteral(value),
                _ => new StringLiteralNode(value?.ToString() ?? "")
            };
        }

        private IntLiteralNode DurationParamLiteral(object? value)
        {
            // A duration param binds as its folded int tick constant at the context tick rate.
            string text = value?.ToString() ?? "0s";
            LanguageResult<IReadOnlyList<Token>> lexed = ExpressionLexer.Tokenize(text);
            if (lexed.Success && lexed.Require() is [{ Kind: TokenKind.DurationLiteral } token, ..])
            {
                double ticksPerUnit = token.Unit == DurationUnit.Milliseconds
                    ? _ctx.TicksPerSecond / 1000.0
                    : _ctx.TicksPerSecond;
                return new IntLiteralNode((long)Math.Round(token.DurationMagnitude * ticksPerUnit,
                    MidpointRounding.AwayFromZero));
            }

            // The "m:ss[.frac]" slot-scalar form folds at the context tick rate too (spec §1).
            if (TryFoldClockDuration(text.Trim(), _ctx.TicksPerSecond, out int clockTicks))
            {
                return new IntLiteralNode(clockTicks);
            }

            return new IntLiteralNode(ToLong(value));
        }

        private static long ToLong(object? value) =>
            value switch
            {
                long l => l,
                int i => i,
                double d => (long)d,
                _ => long.TryParse(value?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long r)
                    ? r
                    : 0
            };

        private static double ToDouble(object? value) =>
            value switch
            {
                double d => d,
                long l => l,
                int i => i,
                _ => double.TryParse(value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double r)
                    ? r
                    : 0
            };

        private static RulesType ParamRulesType(ParamType type) =>
            type switch
            {
                ParamType.Int => RulesType.Int,
                ParamType.Float => RulesType.Float,
                ParamType.Bool => RulesType.Bool,
                ParamType.Duration => RulesType.Duration,
                _ => RulesType.String
            };

        // ── Shadowing / cross-ruleset roots ────────────────────────────────────────

        private void CheckThisShadow()
        {
            foreach (StatDef stat in _doc.Stats)
            {
                if (string.Equals(stat.Id, "this", StringComparison.Ordinal))
                {
                    Report(ResolveDiagnosticCodes.ThisShadowed,
                        "a stat may not be named 'this' — it shadows the self-reference (spec §4)", stat.Position);
                }
            }

            foreach (HighlightDef highlight in _doc.Highlights)
            {
                if (string.Equals(highlight.Id, "this", StringComparison.Ordinal))
                {
                    Report(ResolveDiagnosticCodes.ThisShadowed,
                        "a highlight may not be named 'this' — it shadows the self-reference (spec §4)",
                        highlight.Position);
                }
            }
        }

        private void BuildUsedRulesetRoots()
        {
            if (_exports is null)
            {
                return;
            }

            foreach (string used in _doc.Use)
            {
                if (_exports.TryGetExportedStats(used, out IReadOnlyDictionary<string, RulesType>? exported)
                    && exported is not null)
                {
                    IScopeSymbol[] members = exported
                        .Select(pair => (IScopeSymbol)ScopeSymbol.Stat(pair.Key, pair.Value))
                        .ToArray();
                    _usedRulesetRoots.Add(ScopeSymbol.Namespace(used, members));
                }
            }
        }

        // ── Value-type inference (best-effort pass 1) ──────────────────────────────

        private void InferValueTypes()
        {
            // Seed: kind-determined types are exact; sum/capture/compute get a provisional type so
            // their value selectors can resolve sibling references during the refinement pass.
            foreach (StatDef stat in _doc.Stats)
            {
                _valueTypes[stat.Id] = SeedValueType(stat);
            }

            RebuildSiblingSymbols();

            // Refine sum/capture/compute value/formula types with siblings in scope. Two iterations
            // converge the common chains; a value selector referencing a not-yet-typed sibling keeps
            // its provisional type (a rare forward-value-reference case, acceptable pre-planner).
            for (int iteration = 0; iteration < 2; iteration++)
            {
                bool changed = false;
                foreach (StatDef stat in _doc.Stats)
                {
                    if (stat.Kind is not (StatKind.Sum or StatKind.Capture or StatKind.Compute))
                    {
                        continue;
                    }

                    RulesType inferred = InferValueType(stat);
                    if (!inferred.Equals(_valueTypes[stat.Id]))
                    {
                        _valueTypes[stat.Id] = inferred;
                        changed = true;
                    }
                }

                RebuildSiblingSymbols();
                if (!changed)
                {
                    break;
                }
            }
        }

        private static RulesType SeedValueType(StatDef stat) =>
            stat.Kind switch
            {
                StatKind.Flag or StatKind.Burst => RulesType.Bool, // burst is a bool pulse
                StatKind.Count or StatKind.Tally or StatKind.Streak or StatKind.Bucket => RulesType.Int,
                StatKind.Rate => RulesType.Float, // a per-key ratio is float-valued (G3)
                StatKind.Capture when stat.Keep == KeepMode.List => RulesType.ListOf(RulesTypeKind.Int),
                _ => RulesType.Int
            };

        private RulesType InferValueType(StatDef stat)
        {
            switch (stat.Kind)
            {
                case StatKind.Flag:
                case StatKind.Burst: // burst is a bool pulse
                    return RulesType.Bool;
                case StatKind.Count:
                case StatKind.Tally:
                case StatKind.Streak:
                case StatKind.Bucket:
                    return RulesType.Int;
                case StatKind.Rate:
                    return RulesType.Float;
                case StatKind.Sum:
                case StatKind.Capture:
                {
                    RulesType? scalar = InferSelectorType(stat);
                    RulesType baseType = scalar ?? RulesType.Int;
                    return stat is { Kind: StatKind.Capture, Keep: KeepMode.List }
                        ? RulesType.ListOf(baseType.Kind)
                        : baseType;
                }
                case StatKind.Compute:
                    return InferSelectorType(stat) ?? RulesType.Float;
                default:
                    return RulesType.Int;
            }
        }

        private RulesType? InferSelectorType(StatDef stat)
        {
            if (stat.KindArg is not { } text)
            {
                return null;
            }

            ResolvedTrigger trigger = ResolveTrigger(stat, true);
            IScopeEnvironment scope = stat.Kind == StatKind.Compute
                ? StateScope(null, "value:", []) // best-effort: no siblings yet
                : EventScope(trigger, null, "value:");

            CheckedExpression? checkedExpression = CheckExpression(text, scope, null, true);
            return checkedExpression?.ResultType;
        }

        private void RebuildSiblingSymbols()
        {
            _siblingSymbols.Clear();
            foreach (StatDef stat in _doc.Stats)
            {
                RulesType type = _valueTypes.TryGetValue(stat.Id, out RulesType t) ? t : RulesType.Int;
                bool supportsSet = stat.Kind == StatKind.Capture && type.Kind != RulesTypeKind.List;
                _siblingSymbols.Add(ScopeSymbol.Stat(stat.Id, type, supportsSet));
            }

            foreach (HighlightDef highlight in _doc.Highlights)
            {
                _siblingSymbols.Add(new HighlightScopeSymbol(highlight.Id));
            }
        }

        // ── Stat resolution (pass 2) ───────────────────────────────────────────────

        private CheckedStat? ResolveStat(StatDef stat)
        {
            // tally: is a round-end computation over a sibling value source (no event trigger of its
            // own), so it resolves on a dedicated path rather than the count/streak/bucket trigger one.
            if (stat.Kind == StatKind.Tally)
            {
                return ResolveTally(stat);
            }

            RulesType valueType = _valueTypes.TryGetValue(stat.Id, out RulesType vt) ? vt : RulesType.Int;
            ResolvedTrigger trigger = ResolveTrigger(stat, false);
            if (!trigger.Ok)
            {
                return null;
            }

            // Per-profile coverage skip (build only): the view does not bind on this source.
            IReadOnlyList<string>? concreteEvents = ResolveConcreteEvents(trigger, stat);
            if (concreteEvents is null)
            {
                return null; // coverage-skipped; a RulesetCoverageDiagnostic was recorded
            }

            RulesType thisType = valueType;
            ReadCollector reads = new(this, trigger.View);

            CheckedExpression? condition;
            CheckedExpression? valueSelector;
            if (stat.Kind == StatKind.Compute)
            {
                // compute: is single-AST — the round-end formula IS row 5 (spec §6), not a value
                // selector; it reads round-scoped stats/contexts (state scope), never event.*.
                condition = BuildComputeFormula(stat, thisType, reads);
                valueSelector = null;
            }
            else if (stat.Kind == StatKind.Flag && !trigger.HasEventSource)
            {
                condition = BuildExpressionFlag(stat, thisType, reads); // flag: <predicate> (no on:)
                valueSelector = null;
            }
            else
            {
                condition = BuildTriggerCondition(stat, trigger, thisType, reads);
                valueSelector = BuildValueSelector(stat, trigger, thisType, reads); // sum:/capture: only
            }

            CheckedExpression? whileGate = BuildWhileGate(stat, trigger, thisType, reads);

            RuleNodeKind kind = MapNodeKind(stat.Kind);
            KeepKind keep = MapKeep(stat);
            ScopeAxis scope = ComputeScope(stat.Per, _forEachPlayer);

            // streak/burst kind-args (row 8): fold the window to concrete ticks and default the two so
            // the node's identity is fully determined (an explicit window:640 and its default hash alike).
            // burst reuses the same two carriers (StreakWindow/StreakMinStreak) and defaults.
            bool hasWindowArgs = stat.Kind is StatKind.Streak or StatKind.Burst;
            int? streakWindow = hasWindowArgs ? ResolveStreakWindow(stat) : null;
            int? streakMinStreak = hasWindowArgs ? stat.StreakMinStreak ?? DefaultStreakMinStreak : null;

            // bucket kind-args (row 8): the key-part list (one scalar key or an ordered composite, C8),
            // each type-checked to a string in event scope. The named reducer (reduce:) defaults to sum
            // when a value: is present else count (today's behavior — every pre-C8 bucket is unchanged).
            // A value-reducing reducer (sum/min/max/last/first) requires a value:; count forbids one. The
            // value: selector rides the ValueSelector slot (so it joins row 5 of the resolved-identity
            // preimage; two sum-buckets differing only in value: must NOT dedup), and the reducer name is
            // carried on BucketReducer (a max-bucket and a sum-bucket over the same key+value hash apart).
            // The reducer is normalized so an implicit count and an explicit reduce: count dedup: count →
            // null (byte-identical to the pre-C8 count preimage), every other reducer → its own name.
            IReadOnlyList<string>? bucketKeyParts = null;
            string? bucketReducer = null;
            if (stat.Kind == StatKind.Bucket)
            {
                bucketKeyParts = ResolveBucketKey(stat, trigger, reads);
                bool hasValue = !string.IsNullOrWhiteSpace(stat.BucketValue);
                string effective = stat.BucketReduce ?? (hasValue ? "sum" : "count");
                bool needsValue = effective != "count";

                if (needsValue && !hasValue)
                {
                    Report(ResolveDiagnosticCodes.BadSlotType,
                        $"bucket '{stat.Id}' reduce: {effective} needs a value: to reduce (only count buckets omit value:)",
                        stat.Position);
                }
                else if (!needsValue && hasValue)
                {
                    Report(ResolveDiagnosticCodes.BadSlotType,
                        $"bucket '{stat.Id}' reduce: count takes no value: (a count has nothing to reduce) — "
                        + "omit value:, or choose sum/min/max/last/first",
                        stat.Position);
                }

                if (hasValue)
                {
                    CheckedExpression? bucketValue = ResolveBucketValue(stat, trigger, thisType, reads);
                    if (bucketValue is not null)
                    {
                        valueSelector = bucketValue;
                    }
                }

                bucketReducer = effective == "count" ? null : effective;
            }

            return new CheckedStat(
                _rulesetId,
                stat.Id,
                kind,
                valueType,
                scope,
                concreteEvents,
                condition,
                valueSelector,
                whileGate,
                keep,
                null,
                streakWindow,
                streakMinStreak,
                bucketKeyParts,
                bucketReducer,
                reads.DeclaredReads,
                reads.EntityReads,
                trigger.View?.Name,
                trigger.ActorAny,
                stat.Label,
                stat.Position,
                // Carry the compute's opt-in live: cadence onto the checked stat so the
                // hasher (identity) and the planner (round-end vs live wiring) can see it. Only a
                // compute: mapping sets StatDef.Live; every other kind leaves it false.
                stat.Kind == StatKind.Compute && stat.Live,
                // Carry the compute's display format: through for the planner to stamp on the
                // ComputedStatNode. Presentation only — the hasher never reads it (V2StatHasher.Descriptor
                // omits it), so it is outside node identity, exactly like the display Label.
                Format: stat.Format);
        }

        /// <summary>
        ///     Resolves a <c>bucket:</c> stat's <c>key:</c> (spec §6 row 8):
        ///     each key expression (a single scalar <c>key:</c> or an ordered list of them — a composite
        ///     key, C8), checked to a string in the trigger's event scope, returned in author order as the
        ///     key-part list. Each stored part is the v1-grammar rendering of the checked (normalized)
        ///     AST — identity-bearing for row 8 (distinct parts render distinctly; order is preserved so
        ///     <c>[a, b]</c> ≠ <c>[b, a]</c>) and the exact string the planner feeds to
        ///     <c>CompileEventKeySelector</c> per part. Reads flow into the read set.
        /// </summary>
        private List<string>? ResolveBucketKey(StatDef stat, ResolvedTrigger trigger, ReadCollector reads)
        {
            IReadOnlyList<string> rawParts = stat.BucketKeys is { Count: > 0 } list
                ? list
                : string.IsNullOrWhiteSpace(stat.BucketKey)
                    ? []
                    : [stat.BucketKey];
            if (rawParts.Count == 0)
            {
                return null; // structural validation already reported the missing key
            }

            IScopeEnvironment scope = EventScope(trigger, RulesType.Int, "key:");
            List<string> parts = new(rawParts.Count);
            foreach (string rawPart in rawParts)
            {
                CheckedExpression? key =
                    CheckExpressionText(rawPart, "key:", stat.Position, scope, RulesType.String);
                if (key is null)
                {
                    return null;
                }

                reads.Collect(key);
                parts.Add(V1ExpressionWriter.Write(key.Root));
            }

            return parts;
        }

        /// <summary>
        ///     Resolves a summing <c>bucket:</c> stat's <c>value:</c> — the single-value SUM reducer's
        ///     per-event amount (spec §6 row 8 bucket reducer): checked as a numeric expression in the
        ///     trigger's event scope and returned as a checked AST that rides the
        ///     <see cref="CheckedStat.ValueSelector" /> slot (so it joins row 5 of the resolved-identity
        ///     preimage — two sum-buckets differing only in <c>value:</c> hash apart). Mirrors
        ///     <see cref="BuildValueSelector" />'s <c>sum:</c> typing: the amount must be
        ///     Int/Float/Duration, exactly what the planner's <c>CompileEventValueSelector</c> folds to
        ///     a <c>double</c> delta. Reads flow into the read set, so an enrichment value like
        ///     <c>enrich.hurt.capped_damage</c> orders its enrichment node before the read.
        /// </summary>
        private CheckedExpression? ResolveBucketValue(StatDef stat, ResolvedTrigger trigger, RulesType thisType,
            ReadCollector reads)
        {
            IScopeEnvironment scope = EventScope(trigger, thisType, "value:");
            CheckedExpression? value =
                CheckExpressionText(stat.BucketValue!, "value:", stat.Position, scope, null);
            if (value is null)
            {
                return null;
            }

            if (value.ResultType.Kind is not (RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration))
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"bucket '{stat.Id}' value: is {value.ResultType}, which is not numeric — the SUM reducer needs a number",
                    stat.Position);
            }

            reads.Collect(value);
            return value;
        }

        /// <summary>
        ///     Folds a <c>streak:</c> stat's <c>window:</c> to a concrete tick count: a bare integer is
        ///     ticks; a duration literal (<c>10s</c>, <c>640ms</c>) or the <c>"m:ss[.frac]"</c> clock
        ///     form (e.g. <c>"1:30"</c> = 90s) folds at the context tick rate. An absent or unparseable
        ///     window falls back to the v1 default (a diagnostic is reported for a present-but-unparseable
        ///     value).
        /// </summary>
        private int ResolveStreakWindow(StatDef stat)
        {
            if (string.IsNullOrWhiteSpace(stat.StreakWindow))
            {
                return DefaultStreakWindow;
            }

            string text = stat.StreakWindow.Trim();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks))
            {
                return (int)ticks;
            }

            LanguageResult<IReadOnlyList<Token>> lexed = ExpressionLexer.Tokenize(text);
            if (lexed.Success && lexed.Require() is [{ Kind: TokenKind.DurationLiteral } token, ..])
            {
                double ticksPerUnit = token.Unit == DurationUnit.Milliseconds
                    ? _ctx.TicksPerSecond / 1000.0
                    : _ctx.TicksPerSecond;
                return (int)Math.Round(token.DurationMagnitude * ticksPerUnit, MidpointRounding.AwayFromZero);
            }

            if (TryFoldClockDuration(text, _ctx.TicksPerSecond, out int clockTicks))
            {
                return clockTicks;
            }

            Report(ResolveDiagnosticCodes.BadSlotType,
                $"streak '{stat.Id}' window: '{stat.StreakWindow}' is neither an integer tick count nor a duration",
                stat.Position);
            return DefaultStreakWindow;
        }

        /// <summary>
        ///     Folds the <c>"m:ss[.frac]"</c> YAML slot-scalar duration form (spec §1: a slot scalar,
        ///     not an expression literal) to a tick count at the given rate. <c>"1:30"</c> parses as
        ///     90s (minutes*60 + seconds); the seconds field may carry a fraction (<c>"0:00.5"</c>).
        ///     Returns false for any other shape so the caller can fall through to its own diagnostic.
        /// </summary>
        private static bool TryFoldClockDuration(string text, double ticksPerSecond, out int ticks)
        {
            ticks = 0;
            int colon = text.IndexOf(':');
            if (colon <= 0 || text.IndexOf(':', colon + 1) >= 0)
            {
                return false; // not exactly one ':' with digits on the left
            }

            string minutesPart = text[..colon];
            string secondsPart = text[(colon + 1)..];
            if (!long.TryParse(minutesPart, NumberStyles.None, CultureInfo.InvariantCulture, out long minutes)
                || !double.TryParse(secondsPart, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                    out double seconds)
                || seconds < 0)
            {
                return false;
            }

            double totalSeconds = minutes * 60.0 + seconds;
            ticks = (int)Math.Round(totalSeconds * ticksPerSecond, MidpointRounding.AwayFromZero);
            return true;
        }

        /// <summary>
        ///     Resolves a <c>tally:</c> stat (spec §6 row 8): a round-end
        ///     computation reading a sibling value source (the kind value) and bumping the first
        ///     threshold bucket whose min it meets. It carries the checked source reference as the
        ///     <see cref="CheckedStat.ValueSelector" /> (so two tallies over different sources hash
        ///     apart via row 5+6) and the <c>(min, target)</c> pairs as
        ///     <see cref="CheckedStat.TallyThresholds" /> (row 8). It has no event trigger of its own —
        ///     the planner wires it onto the profile's round-end events.
        /// </summary>
        private CheckedStat? ResolveTally(StatDef stat)
        {
            if (string.IsNullOrWhiteSpace(stat.KindArg) || stat.Thresholds is not { Count: > 0 } thresholds)
            {
                // Structural validation already reported the missing source/thresholds; nothing to build.
                return null;
            }

            ReadCollector reads = new(this, null);
            IScopeEnvironment scope = StateScope(RulesType.Int, "tally:", []);
            CheckedExpression? source =
                CheckExpressionText(stat.KindArg, "tally:", stat.Position, scope, RulesType.Int);
            if (source is null)
            {
                return null;
            }

            reads.Collect(source);

            List<(int Min, string Target)> tallyThresholds = [];
            foreach (TallyThreshold threshold in thresholds)
            {
                // A param-valued min resolves to its literal int BEFORE this list is built (pre-hash),
                // so `min: params.x` with x=3 produces the identical (3, target) pair — and thus the
                // identical resolved-identity hash — as a literal `min: 3` (spec §6 row 8).
                if (!TryResolveTallyMin(threshold, out int minValue))
                {
                    // The bad-min diagnostic is attributed; drop the tally rather than emit a bad node.
                    return null;
                }

                tallyThresholds.Add((minValue, threshold.Target));
            }

            ScopeAxis tallyScope = ComputeScope(stat.Per, _forEachPlayer);

            return new CheckedStat(
                _rulesetId,
                stat.Id,
                RuleNodeKind.Tally,
                RulesType.Int,
                tallyScope,
                [], // no trigger of its own — the planner wires it onto round-end events
                null,
                source, // the value source rides the value-selector slot
                null,
                KeepKind.None,
                tallyThresholds,
                null,
                null,
                null,
                null,
                reads.DeclaredReads,
                reads.EntityReads,
                null,
                false,
                stat.Label,
                stat.Position);
        }

        /// <summary>
        ///     Resolves a <c>rate:</c> stat (G3 per-key ratios): a derived per-key ratio over two sibling
        ///     <c>bucket:</c> stats — <c>of:</c> (numerator) over <c>per:</c> (denominator), evaluated per
        ///     denominator key. It has no event trigger of its own (the planner builds a
        ///     <c>KeyedRatioNode</c> reading the two already-built bucket nodes). Both refs must be numeric
        ///     sibling buckets keying on <b>identical</b> key-parts, else their key spaces aren't comparable.
        ///     <para>
        ///         <b>Identity (the clean way):</b> the resolver synthesizes an <c>of / per</c> division
        ///         <see cref="CheckedExpression" /> into the stat's <see cref="CheckedStat.TriggerCondition" />
        ///         (row 5) slot, so <see cref="Compile.V2StatHasher" /> / <c>ExpressionHasher</c>'s row-6
        ///         reference-incorporation makes two rates over different bucket pairs hash apart FOR FREE —
        ///         no new preimage row, no preimage-shape change.
        ///     </para>
        /// </summary>
        private CheckedStat? ResolveRate(StatDef stat, Dictionary<string, CheckedStat> checkedById)
        {
            string? ofId = stat.RateOf?.Trim();
            string? perId = stat.RatePer?.Trim();
            if (string.IsNullOrWhiteSpace(ofId) || string.IsNullOrWhiteSpace(perId))
            {
                // Structural validation already reported the missing of:/per:; nothing to build.
                return null;
            }

            if (!ValidateRateBucketRef(stat, "of", ofId, out StatDef? ofDef)
                | !ValidateRateBucketRef(stat, "per", perId, out StatDef? perDef))
            {
                return null;
            }

            // A referenced bucket that isn't in checkedById resolved to nothing (coverage-skipped on this
            // profile). The population base is gone, so the rate is coverage-skipped too — silently, with
            // no attributed error (the bucket's own coverage diagnostic already explains the skip).
            if (!checkedById.TryGetValue(ofId, out CheckedStat? ofStat)
                || !checkedById.TryGetValue(perId, out CheckedStat? perStat))
            {
                return null;
            }

            // Numeric gate (a bucket is Int by construction, but keep the check honest and attributed).
            if (!IsNumeric(ofStat.ValueType) || !IsNumeric(perStat.ValueType))
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"rate '{stat.Id}' of:/per: must be numeric buckets", stat.Position);
                return null;
            }

            // Identical key-parts: the two key spaces must line up, else a numerator key can never match a
            // denominator key (the ratio would be uniformly 0). Compare the resolved (normalized) key-part
            // lists ordinally and order-bearingly.
            if (!KeyPartsEqual(ofStat.BucketKeyParts, perStat.BucketKeyParts))
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"rate '{stat.Id}' of: '{ofId}' and per: '{perId}' key on different key: parts — "
                    + "a per-key rate needs both buckets keyed identically", stat.Position);
                return null;
            }

            // Synthesize the of / per division into the row-5 expression slot (identity for free). The two
            // ids resolve as sibling stat references in state scope, so the checked expression carries their
            // resolved-reference identities — ExpressionHasher row 6 embeds each bucket's own node hash.
            ReadCollector reads = new(this, null);
            IScopeEnvironment scope = StateScope(RulesType.Float, "rate:", []);
            CheckedExpression? ratio =
                CheckExpressionText($"{ofId} / {perId}", "rate:", stat.Position, scope, null);
            if (ratio is null)
            {
                return null;
            }

            reads.Collect(ratio);
            ScopeAxis rateScope = ComputeScope(stat.Per, _forEachPlayer);

            return new CheckedStat(
                _rulesetId,
                stat.Id,
                RuleNodeKind.Rate,
                RulesType.Float,
                rateScope,
                [], // derived — no trigger events of its own
                ratio, // the of / per division rides row 5 (identity via row-6 reference embedding)
                null,
                null,
                KeepKind.None,
                null,
                null,
                null,
                null,
                null,
                reads.DeclaredReads,
                reads.EntityReads,
                null,
                false,
                stat.Label,
                stat.Position,
                false,
                ofId,
                perId);
        }

        /// <summary>
        ///     Validates that a <c>rate:</c> ref names a sibling <c>bucket:</c> stat (G3). Reports an
        ///     attributed error and returns <c>false</c> when the ref is unknown or is a non-bucket kind.
        /// </summary>
        private bool ValidateRateBucketRef(StatDef stat, string slot, string refId,
            [NotNullWhen(true)] out StatDef? bucketDef)
        {
            if (!_statsById.TryGetValue(refId, out bucketDef))
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"rate '{stat.Id}' {slot}: '{refId}' is not a sibling stat", stat.Position);
                return false;
            }

            if (bucketDef.Kind != StatKind.Bucket)
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"rate '{stat.Id}' {slot}: '{refId}' is a '{bucketDef.Kind.ToString().ToLowerInvariant()}', "
                    + "not a bucket — a rate is a ratio of two same-keyed buckets", stat.Position);
                bucketDef = null;
                return false;
            }

            return true;
        }

        private static bool IsNumeric(RulesType type) =>
            type.Kind is RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration;

        private static bool KeyPartsEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b)
        {
            if (a is null || b is null || a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     Resolves a tally threshold's <c>min:</c> to its concrete int (spec §6 row 8). A literal
        ///     min passes through; a <c>params.&lt;name&gt;</c> min binds to the param's literal int value
        ///     via the same source <see cref="ParamInliner" /> uses (<see cref="ParamLiteral" />), so it
        ///     folds pre-hash and dedups with the equivalent literal min. An undeclared param, a
        ///     non-<c>int</c> param, or a malformed reference is an attributed error (returns
        ///     <c>false</c>).
        /// </summary>
        private bool TryResolveTallyMin(TallyThreshold threshold, out int min)
        {
            min = 0;
            switch (threshold.Min)
            {
                case TallyMinLiteral literal:
                    min = literal.Value;
                    return true;

                case TallyMinParam param:
                    return TryResolveParamMin(param.RawText, threshold.Position, out min);

                default:
                    return false;
            }
        }

        private bool TryResolveParamMin(string rawText, SourcePosition position, out int min)
        {
            min = 0;

            string? name = ExtractParamName(rawText);
            if (name is null)
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"tally threshold 'min: {rawText}' must be an integer literal or a 'params.<name>' "
                    + "reference (spec §6 row 8)", position);
                return false;
            }

            if (!_paramDefsByName.TryGetValue(name, out ParamDef? param))
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"tally threshold 'min' references undeclared param '{name}' (spec §6 row 8)", position);
                return false;
            }

            if (param.Type != ParamType.Int)
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"tally threshold 'min' param '{name}' must be int, not "
                    + $"{param.Type.ToString().ToLowerInvariant()} (spec §6 row 8)", position);
                return false;
            }

            // Bind to the literal int the same way ParamInliner does — bound value in build mode, the
            // declared default otherwise — folding before the (min, target) pair is hashed.
            if (ParamLiteral(param) is IntLiteralNode intLiteral)
            {
                min = (int)intLiteral.Value;
                return true;
            }

            Report(ResolveDiagnosticCodes.BadSlotType,
                $"tally threshold 'min' param '{name}' did not fold to an integer value (spec §6 row 8)",
                position);
            return false;
        }

        /// <summary>
        ///     Extracts the declared-param name from a tally-min reference text: the qualified
        ///     <c>params.&lt;name&gt;</c> form (mirroring <see cref="ParamInliner" />'s pilot spelling) or a
        ///     bare declared-param identifier. Returns <c>null</c> when the text is not a simple param
        ///     reference (e.g. it carries operators, a member tail, or is non-numeric garbage).
        /// </summary>
        private static string? ExtractParamName(string rawText)
        {
            string text = rawText.Trim();
            const string Prefix = "params.";
            if (text.StartsWith(Prefix, StringComparison.Ordinal))
            {
                text = text[Prefix.Length..];
            }

            return IsSimpleIdentifier(text) ? text : null;
        }

        private static bool IsSimpleIdentifier(string text)
        {
            if (text.Length == 0 || !(char.IsLetter(text[0]) || text[0] == '_'))
            {
                return false;
            }

            foreach (char c in text)
            {
                if (!(char.IsLetterOrDigit(c) || c == '_'))
                {
                    return false;
                }
            }

            return true;
        }

        private CheckedExpression? BuildExpressionFlag(StatDef stat, RulesType thisType, ReadCollector reads)
        {
            if (stat.KindArg is not { } predicate)
            {
                return null;
            }

            IScopeEnvironment scope = StateScope(thisType, "when:", []);
            CheckedExpression? checkedExpression =
                CheckExpressionText(predicate, "when:", stat.Position, scope, RulesType.Bool);
            reads.Collect(checkedExpression);
            return checkedExpression;
        }

        private CheckedExpression? BuildTriggerCondition(StatDef stat, ResolvedTrigger trigger, RulesType thisType,
            ReadCollector reads)
        {
            List<ExpressionNode> conjuncts = [];

            // 1. merged match: bindings, catalog facet order (spec §4.2 / §5 row 5).
            foreach ((CatalogFacet facet, UnaryTest test) in trigger.MergedMatch)
            {
                conjuncts.Add(MatchLowering.Lower(_adapter.FacetRead(facet), test));
            }

            // 1b. net.<Message> field-facet match: bindings lower to event.<Field> reads over the
            // message payload (D12). Same slot as a view's facets (nothing precedes them for a net
            // trigger — no facets/baked), so a structured match: and a free-form where: hash alike.
            foreach (MatchBinding binding in trigger.NetMatch)
            {
                conjuncts.Add(MatchLowering.Lower(new ReferenceNode(["event", binding.Key]), binding.Test));
            }

            // 2. view baked: filters (views.yaml order).
            if (trigger.View is { } view)
            {
                foreach (string baked in view.Baked)
                {
                    ExpressionNode? parsed = ParseOrReport(baked, "baked:", stat.Position);
                    if (parsed is not null)
                    {
                        conjuncts.Add(parsed);
                    }
                }
            }

            // 3. define where:, then 4. site where:.
            AppendWhere(trigger.DefineWhere, "where:", stat.Position, conjuncts);
            AppendWhere(trigger.SiteWhere, "where:", stat.Position, conjuncts);

            if (conjuncts.Count == 0)
            {
                return null;
            }

            ExpressionNode composed = conjuncts.Aggregate((left, right) =>
                new BinaryNode(BinaryOperator.And, left, right));
            IScopeEnvironment scope = EventScope(trigger, thisType, "where:");
            CheckedExpression? checkedExpression = CheckNode(composed, scope, RulesType.Bool, "where:", stat.Position);
            reads.Collect(checkedExpression);
            return checkedExpression;
        }

        private void AppendWhere(string? whereText, string slot, SourcePosition pos, List<ExpressionNode> conjuncts)
        {
            if (whereText is null)
            {
                return;
            }

            ExpressionNode? parsed = ParseOrReport(whereText, slot, pos);
            if (parsed is not null)
            {
                conjuncts.Add(ParamInliner.Inline(parsed, _paramLiterals));
            }
        }

        private CheckedExpression? BuildValueSelector(StatDef stat, ResolvedTrigger trigger, RulesType thisType,
            ReadCollector reads)
        {
            if (stat.Kind is not (StatKind.Sum or StatKind.Capture) || stat.KindArg is not { } text)
            {
                return null;
            }

            string slot = stat.Kind == StatKind.Sum ? "sum:" : "capture:";
            IScopeEnvironment scope = EventScope(trigger, thisType, slot);
            CheckedExpression? checkedExpression = CheckExpressionText(text, slot, stat.Position, scope, null);

            if (checkedExpression is not null)
            {
                ValidateSelectorType(stat, checkedExpression);
            }

            reads.Collect(checkedExpression);
            return checkedExpression;
        }

        private CheckedExpression? BuildComputeFormula(StatDef stat, RulesType thisType, ReadCollector reads)
        {
            if (stat.KindArg is not { } text)
            {
                return null;
            }

            IScopeEnvironment scope = StateScope(thisType, "compute:", []);
            CheckedExpression? formula = CheckExpressionText(text, "compute:", stat.Position, scope, null);
            if (formula is not null && formula.ResultType.Kind
                    is not (RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration))
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"stat '{stat.Id}' (compute) value is {formula.ResultType}, which is not numeric", stat.Position);
            }

            reads.Collect(formula);
            return formula;
        }

        private void ValidateSelectorType(StatDef stat, CheckedExpression expression)
        {
            RulesTypeKind kind = expression.ResultType.Kind;
            bool ok = stat.Kind switch
            {
                StatKind.Sum => kind is RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration,
                // keep: min | max reduce an orderable numeric value (mirroring the bucket min/max
                // reducer's numeric gate); a string/bool/list capture value is a type error. Instant
                // is included — a min/max over ticks (earliest/latest event tick) is a natural,
                // orderable operation, unlike a bucket value: SUM amount (where adding ticks is
                // nonsensical, so Instant is excluded there). Other keeps accept any scalar.
                StatKind.Capture when stat.Keep is KeepMode.Min or KeepMode.Max =>
                    kind is RulesTypeKind.Int or RulesTypeKind.Float or RulesTypeKind.Duration
                        or RulesTypeKind.Instant,
                StatKind.Capture => kind is not (RulesTypeKind.List or RulesTypeKind.Map or RulesTypeKind.None),
                _ => true
            };

            if (!ok)
            {
                Report(ResolveDiagnosticCodes.BadSlotType,
                    $"stat '{stat.Id}' ({stat.Kind.ToString().ToLowerInvariant()}) value is {expression.ResultType}, "
                    + "which is not valid for this kind", stat.Position);
            }
        }

        private CheckedExpression? BuildWhileGate(StatDef stat, ResolvedTrigger trigger, RulesType thisType,
            ReadCollector reads)
        {
            if (trigger.Whiles.Count == 0)
            {
                return null;
            }

            List<ExpressionNode> gates = [];
            foreach (string whileText in trigger.Whiles)
            {
                ExpressionNode? parsed = ParseOrReport(whileText, "while:", stat.Position);
                if (parsed is not null)
                {
                    gates.Add(ParamInliner.Inline(parsed, _paramLiterals));
                }
            }

            if (gates.Count == 0)
            {
                return null;
            }

            ExpressionNode composed = gates.Aggregate((left, right) =>
                new BinaryNode(BinaryOperator.And, left, right));
            IScopeEnvironment scope = StateScope(thisType, "while:", []);
            CheckedExpression? checkedExpression = CheckNode(composed, scope, RulesType.Bool, "while:", stat.Position);
            reads.Collect(checkedExpression);
            return checkedExpression;
        }

        // ── Highlight resolution ───────────────────────────────────────────────────

        private CheckedHighlight? ResolveHighlight(HighlightDef highlight)
        {
            ReadCollector reads = new(this, null);
            IScopeEnvironment scope = StateScope(null, "when:", []);
            CheckedExpression? when = CheckExpressionText(highlight.When, "when:", highlight.Position, scope,
                RulesType.Bool);
            if (when is null)
            {
                return null;
            }

            reads.Collect(when);
            ScopeAxis scopeAxis = ComputeScope(highlight.Per, _forEachPlayer);
            ScopeAxis countScope = _forEachPlayer ? ScopeAxis.PlayerMatch : ScopeAxis.Match;

            int score = highlight.Score ?? DefaultHighlightScore;
            HighlightKind kind = ResolveHighlightKind(highlight);
            // Trim to null: an empty/whitespace group is "ungrouped", never a distinct family of one.
            string? group = string.IsNullOrWhiteSpace(highlight.Group) ? null : highlight.Group.Trim();

            return new CheckedHighlight(_rulesetId, highlight.Id, scopeAxis, countScope, when, highlight.Title,
                score, kind, group, reads.DeclaredReads, reads.EntityReads, highlight.Position);
        }

        /// <summary>
        ///     Maps a highlight's raw <c>kind:</c> text to <see cref="HighlightKind" />, defaulting to
        ///     <see cref="HighlightKind.Highlight" /> when absent and reporting
        ///     <see cref="ResolveDiagnosticCodes.BadHighlightKind" /> (then falling back to the default)
        ///     on an unrecognized value — the "default + Report on bad input" shape of
        ///     <c>ResolveStreakWindow</c>.
        /// </summary>
        private HighlightKind ResolveHighlightKind(HighlightDef highlight)
        {
            if (string.IsNullOrWhiteSpace(highlight.Kind))
            {
                return HighlightKind.Highlight;
            }

            switch (highlight.Kind.Trim())
            {
                case "highlight": return HighlightKind.Highlight;
                case "funny": return HighlightKind.Funny;
                case "lowlight": return HighlightKind.Lowlight;
                case "hidden": return HighlightKind.Hidden;
                default:
                    Report(ResolveDiagnosticCodes.BadHighlightKind,
                        $"highlight '{highlight.Id}' has kind: '{highlight.Kind}' — "
                        + "expected one of highlight | funny | lowlight | hidden",
                        highlight.Position);
                    return HighlightKind.Highlight;
            }
        }

        // ── Trigger source resolution ──────────────────────────────────────────────

        private ResolvedTrigger ResolveTrigger(StatDef stat, bool silent)
        {
            TriggerRef? sourceRef = stat.Kind switch
            {
                StatKind.Count or StatKind.Tally or StatKind.Streak or StatKind.Bucket or StatKind.Burst =>
                    ParseTriggerRefText(stat.KindArg, stat.Position),
                StatKind.Sum or StatKind.Capture or StatKind.Flag => stat.Trigger?.On,
                _ => null // compute has no trigger
            };

            ResolvedTrigger resolved = new();

            // Splice a trigger-bodied define.
            if (sourceRef is { Kind: TriggerRefKind.ViewOrDefine } vod
                && _definesByName.TryGetValue(vod.Name, out DefineDef? define))
            {
                if (define.Body is TriggerDefineBody triggerBody)
                {
                    MergeDefineTrigger(triggerBody.Trigger, resolved, silent);
                    sourceRef = triggerBody.Trigger.On;
                }
                else if (!silent)
                {
                    Report(ResolveDiagnosticCodes.DefineInExpression,
                        $"'{vod.Name}' is a list/expression define, not a trigger — it cannot be a trigger source",
                        stat.Position);
                    return resolved;
                }
            }

            ResolveSource(sourceRef, stat, resolved, silent);
            MergeSiteRefinements(stat.Trigger, resolved, silent);
            FinalizeMatch(resolved, stat.Position, silent);
            return resolved;
        }

        private void ResolveSource(TriggerRef? sourceRef, StatDef stat, ResolvedTrigger resolved, bool silent)
        {
            if (sourceRef is null)
            {
                resolved.Ok = stat.Kind is StatKind.Flag or StatKind.Compute; // expression flag / compute need no source
                return;
            }

            switch (sourceRef.Kind)
            {
                case TriggerRefKind.Raw:
                    resolved.RawOrNetName = sourceRef.Name;
                    resolved.Ok = true;
                    return;
                case TriggerRefKind.Net:
                    resolved.RawOrNetName = sourceRef.Name;
                    resolved.IsNet = true;
                    resolved.Ok = true;
                    return;
                case TriggerRefKind.This:
                    if (!silent)
                    {
                        Report(ResolveDiagnosticCodes.UnknownTriggerSource,
                            "'this' is the self-reference and cannot be a trigger source", stat.Position);
                    }

                    return;
            }

            string name = sourceRef.Name;
            if (_viewsByName.TryGetValue(name, out CatalogView? view))
            {
                resolved.View = view;
                resolved.Ok = true;
                return;
            }

            if (stat.Kind == StatKind.Count && _statsById.TryGetValue(name, out StatDef? flag)
                                            && flag.Kind == StatKind.Flag)
            {
                resolved.FlagSource = name;
                resolved.Ok = true;
                return;
            }

            if (!silent)
            {
                Report(ResolveDiagnosticCodes.UnknownTriggerSource,
                    $"'{name}' is not a known view, trigger define, or sibling flag", stat.Position);
            }
        }

        private void MergeDefineTrigger(TriggerDef trigger, ResolvedTrigger resolved, bool silent)
        {
            AddMatch(trigger.Match, resolved, true, silent);
            if (trigger.Where is { } where)
            {
                resolved.DefineWhere = where;
            }

            if (trigger.While is { } gate)
            {
                resolved.Whiles.Add(gate);
            }

            if (trigger.Actor is not null)
            {
                resolved.ActorAny = true;
            }
        }

        private void MergeSiteRefinements(TriggerDef? trigger, ResolvedTrigger resolved, bool silent)
        {
            if (trigger is null)
            {
                return;
            }

            AddMatch(trigger.Match, resolved, false, silent);
            if (trigger.Where is { } where)
            {
                resolved.SiteWhere = where;
            }

            if (trigger.While is { } gate)
            {
                resolved.Whiles.Add(gate);
            }

            if (trigger.Actor is not null)
            {
                resolved.ActorAny = true;
            }
        }

        private void AddMatch(IReadOnlyList<MatchBinding> bindings, ResolvedTrigger resolved, bool isDefine,
            bool silent)
        {
            foreach (MatchBinding binding in bindings)
            {
                if (!resolved.MatchKeys.Add(binding.Key))
                {
                    if (!silent)
                    {
                        Report(ResolveDiagnosticCodes.DuplicateMatchKey,
                            $"match: key '{binding.Key}' is set in both the define and the site — no silent last-wins (spec §4.2)",
                            binding.Position);
                    }

                    continue;
                }

                resolved.PendingMatch.Add(binding);
            }
        }

        /// <summary>Resolves the pending match bindings against the view's facets (needs the view resolved first).</summary>
        private void FinalizeMatch(ResolvedTrigger resolved, SourcePosition pos, bool silent)
        {
            if (resolved.PendingMatch.Count == 0)
            {
                return;
            }

            // A net.<Message> trigger has no curated view/facets, so match: is a field-facet form
            // over the message payload: each `{ Field: <test> }` binding lowers to an
            // `event.<Field> <op> <value>` where:-conjunct (net payload matching). The
            // conjuncts are type-checked against the net-message event scope in BuildTriggerCondition,
            // so an unknown field / wrong-type test surfaces the same attributed error a free-form
            // `where:` field read would. A structured `match: { F: v }` and a free-form
            // `where: "event.F == v"` therefore lower to the identical AST and hash alike.
            if (resolved.IsNet)
            {
                resolved.NetMatch.AddRange(resolved.PendingMatch);
                return;
            }

            if (resolved.View is not { } view)
            {
                if (!silent)
                {
                    Report(ResolveDiagnosticCodes.UnknownFacet,
                        "match: is only valid on a view or net.<Message> trigger (raw triggers have no facets)", pos);
                }

                return;
            }

            Dictionary<string, (CatalogFacet Facet, int Order)> facets = new(StringComparer.Ordinal);
            for (int i = 0; i < view.Facets.Count; i++)
            {
                facets[view.Facets[i].Name] = (view.Facets[i], i);
            }

            List<(int Order, CatalogFacet Facet, UnaryTest Test)> lowered = [];
            foreach (MatchBinding binding in resolved.PendingMatch)
            {
                if (facets.TryGetValue(binding.Key, out (CatalogFacet Facet, int Order) facet))
                {
                    lowered.Add((facet.Order, facet.Facet, binding.Test));
                }
                else if (!silent)
                {
                    string known = string.Join(", ", view.Facets.Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal));
                    Report(ResolveDiagnosticCodes.UnknownFacet,
                        $"'{binding.Key}' is not a facet of view '{view.Name}' — known facets: {known}", binding.Position);
                }
            }

            resolved.MergedMatch = lowered.OrderBy(entry => entry.Order)
                .Select(entry => (entry.Facet, entry.Test)).ToList();
        }

        private static TriggerRef? ParseTriggerRefText(string? text, SourcePosition pos)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            string trimmed = text.Trim();
            if (string.Equals(trimmed, "this", StringComparison.Ordinal))
            {
                return new TriggerRef(TriggerRefKind.This, "this", pos);
            }

            if (trimmed.StartsWith("raw.", StringComparison.Ordinal))
            {
                return new TriggerRef(TriggerRefKind.Raw, trimmed["raw.".Length..], pos);
            }

            if (trimmed.StartsWith("net.", StringComparison.Ordinal))
            {
                return new TriggerRef(TriggerRefKind.Net, trimmed["net.".Length..], pos);
            }

            return new TriggerRef(TriggerRefKind.ViewOrDefine, trimmed, pos);
        }

        // ── Concrete events / coverage ─────────────────────────────────────────────

        private IReadOnlyList<string>? ResolveConcreteEvents(ResolvedTrigger trigger, StatDef stat)
        {
            if (trigger.RawOrNetName is { } rawOrNet)
            {
                return [rawOrNet];
            }

            if (trigger.FlagSource is not null)
            {
                return []; // count-on-flag: the flag carries the events; the count rides its rising edge
            }

            if (trigger.View is not { } view)
            {
                return []; // expression flag / compute: no direct events
            }

            if (!_ctx.IsBuild)
            {
                return [view.Event]; // demo-less draft: the logical event stands in
            }

            foreach (CatalogViewProfile profile in view.Profiles)
            {
                if (string.Equals(profile.Profile, _ctx.ProfileId, StringComparison.Ordinal))
                {
                    if (profile.ConcreteEvents.Count > 0)
                    {
                        return [.. profile.ConcreteEvents];
                    }

                    _coverage.Add(new RulesetCoverageDiagnostic(_rulesetId, stat.Id, view.Name, _ctx.ProfileId!,
                        $"stat '{stat.Id}' uses view '{view.Name}', which does not bind on source profile "
                        + $"'{_ctx.ProfileId}' — skipped (no wire event on this source)", stat.Position));
                    return null;
                }
            }

            _coverage.Add(new RulesetCoverageDiagnostic(_rulesetId, stat.Id, view.Name, _ctx.ProfileId!,
                $"stat '{stat.Id}' uses view '{view.Name}', which has no binding for profile '{_ctx.ProfileId}' — skipped",
                stat.Position));
            return null;
        }

        // ── Scope assembly ─────────────────────────────────────────────────────────

        private ScopeEnvironment EventScope(ResolvedTrigger trigger, RulesType? thisType, string slot) =>
            EventScope(trigger.View, trigger.IsNet ? trigger.RawOrNetName : null, thisType, slot,
                trigger.RoleNames);

        private ScopeEnvironment EventScope(CatalogView? view, string? netMessage, RulesType? thisType,
            string slot, IReadOnlyList<string> roleNames)
        {
            Dictionary<string, IScopeSymbol> roots = new(StringComparer.Ordinal);

            // A net.<Message> trigger exposes its payload fields under event.* from the netMessages
            // catalog family (D12 payload matching); a view trigger exposes its wire event's fields.
            IScopeSymbol eventNamespace = netMessage is not null
                ? _adapter.NetMessageNamespace(netMessage)
                : _adapter.EventNamespace(view?.Event ?? "");
            AddRoot(roots, eventNamespace);
            AddRoot(roots, _adapter.Enrich);
            foreach (string role in roleNames)
            {
                AddRoot(roots, _adapter.RoleNamespace(role));
            }

            AddCommonRoots(roots, thisType);
            return new ScopeEnvironment(slot, roots.Values);
        }

        private ScopeEnvironment StateScope(RulesType? thisType, string slot, IReadOnlyList<string> _)
        {
            Dictionary<string, IScopeSymbol> roots = new(StringComparer.Ordinal);
            AddCommonRoots(roots, thisType);
            return new ScopeEnvironment(slot, roots.Values);
        }

        private void AddCommonRoots(Dictionary<string, IScopeSymbol> roots, RulesType? thisType)
        {
            if (_forEachPlayer)
            {
                AddRoot(roots, _adapter.Player);
            }

            AddRoot(roots, _adapter.Round);
            AddRoot(roots, _adapter.Match);
            if (_paramsNamespace is not null)
            {
                AddRoot(roots, _paramsNamespace);
            }

            foreach (IScopeSymbol used in _usedRulesetRoots)
            {
                AddRoot(roots, used);
            }

            foreach (IScopeSymbol sibling in _siblingSymbols)
            {
                AddRoot(roots, sibling);
            }

            if (thisType is { } type)
            {
                AddRoot(roots, ScopeSymbol.Value("this", type));
            }
        }

        private static void AddRoot(Dictionary<string, IScopeSymbol> roots, IScopeSymbol symbol) =>
            roots.TryAdd(symbol.Name, symbol);

        // ── Expression checking helpers ────────────────────────────────────────────

        private CheckedExpression? CheckExpressionText(string text, string slot, SourcePosition pos,
            IScopeEnvironment scope, RulesType? expected)
        {
            ExpressionNode? parsed = ParseOrReport(text, slot, pos);
            if (parsed is null)
            {
                return null;
            }

            ExpressionNode inlined = ParamInliner.Inline(parsed, _paramLiterals);
            return CheckNode(inlined, scope, expected, slot, pos);
        }

        private CheckedExpression? CheckNode(ExpressionNode node, IScopeEnvironment scope, RulesType? expected,
            string slot, SourcePosition pos)
        {
            NormalizerOptions options = new()
            {
                TicksPerSecond = _ctx.TicksPerSecond,
                DefineLookup = DefineLookup
            };

            LanguageResult<ExpressionNode> normalized = ExpressionNormalizer.Normalize(node, options);
            if (!normalized.Success)
            {
                ReportCore(normalized.Diagnostics, slot, pos);
                return null;
            }

            LanguageResult<CheckedExpression> checkedExpression =
                ExpressionChecker.Check(normalized.Require(), scope, expected);
            if (!checkedExpression.Success)
            {
                ReportCore(checkedExpression.Diagnostics, slot, pos);
                return null;
            }

            return checkedExpression.Require();
        }

        /// <summary>Best-effort check used for pass-1 type inference: diagnostics are discarded.</summary>
        private CheckedExpression? CheckExpression(string text, IScopeEnvironment scope, RulesType? expected,
            bool silent)
        {
            LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(text);
            if (!parsed.Success)
            {
                return null;
            }

            ExpressionNode inlined = ParamInliner.Inline(parsed.Require(), _paramLiterals);
            NormalizerOptions options = new()
            {
                TicksPerSecond = _ctx.TicksPerSecond,
                DefineLookup = DefineLookup
            };
            LanguageResult<ExpressionNode> normalized = ExpressionNormalizer.Normalize(inlined, options);
            if (!normalized.Success)
            {
                return null;
            }

            LanguageResult<CheckedExpression> result = ExpressionChecker.Check(normalized.Require(), scope, expected);
            return result.Success ? result.Require() : null;
        }

        private ExpressionNode? ParseOrReport(string text, string slot, SourcePosition pos)
        {
            LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(text);
            if (parsed.Success)
            {
                return parsed.Require();
            }

            ReportCore(parsed.Diagnostics, slot, pos);
            return null;
        }

        private ExpressionNode? DefineLookup(string head)
        {
            if (!_definesByName.TryGetValue(head, out DefineDef? define))
            {
                return null;
            }

            switch (define.Body)
            {
                case ListDefineBody list:
                {
                    ImmutableArray<ExpressionNode>.Builder builder = ImmutableArray.CreateBuilder<ExpressionNode>(
                        list.Items.Count);
                    foreach (string item in list.Items)
                    {
                        builder.Add(ScalarLiteral(item));
                    }

                    return new ListLiteralNode(builder.MoveToImmutable());
                }
                case ExpressionDefineBody expression:
                {
                    LanguageResult<ExpressionNode> parsed = ExpressionParser.Parse(expression.Text);
                    return parsed.Success ? ParamInliner.Inline(parsed.Require(), _paramLiterals) : null;
                }
                case MapDefineBody map:
                {
                    // Inline a map define as a constant map literal; `ref[key]` (an IndexAccessNode over
                    // this node) checks as map<T> and evaluates to the mapped value or null on a miss.
                    ImmutableArray<MapEntry>.Builder builder = ImmutableArray.CreateBuilder<MapEntry>(map.Entries.Count);
                    foreach (MapDefineEntry entry in map.Entries)
                    {
                        builder.Add(new MapEntry(entry.Key, MapValueLiteral(entry.Value)));
                    }

                    return new MapLiteralNode(builder.MoveToImmutable());
                }
                default:
                    return null; // trigger-bodied define used in expression position → unknown-root
            }
        }

        private static ExpressionNode ScalarLiteral(string raw)
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long asLong))
            {
                return new IntLiteralNode(asLong);
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble))
            {
                return new FloatLiteralNode(asDouble);
            }

            return raw switch
            {
                "true" => new BoolLiteralNode(true),
                "false" => new BoolLiteralNode(false),
                _ => new StringLiteralNode(raw)
            };
        }

        /// <summary>
        ///     A map define value literal: a number (int/float) or a string — never a bool or null
        ///     (map values are all-number or all-string, spec §3.4). Unlike <see cref="ScalarLiteral" />,
        ///     <c>true</c>/<c>false</c> are treated as string values, not bools.
        /// </summary>
        private static ExpressionNode MapValueLiteral(string raw)
        {
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long asLong))
            {
                return new IntLiteralNode(asLong);
            }

            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double asDouble)
                ? new FloatLiteralNode(asDouble)
                : new StringLiteralNode(raw);
        }

        private bool TryEntityRead(ResolvedReference reference, CatalogView? view,
            [NotNullWhen(true)] out EntityProviderReference? entity)
        {
            // player.* / match.* entity provider (singleton or per-player keyed by the ruleset player).
            if (_providerByV2Name.TryGetValue(reference.Path, out CatalogProvider? provider))
            {
                entity = new EntityProviderReference(reference.Path, provider.Name, EntityProviderReference.PlayerSubject);
                return true;
            }

            // Role handle: <role>.<member> where the role is one of the view's roles. The role
            // resolves to its event slot-field (killer → Attacker) via the view's roles table, threaded
            // onto the reference so the planner emits `<SlotField>.<provider>` — the read of the ROLE's
            // per-fire entity value, not the ruleset player's.
            string[] segments = reference.Path.Split('.');
            if (view is not null && segments.Length == 2
                                 && view.Roles.FirstOrDefault(r => string.Equals(r.Role, segments[0], StringComparison.Ordinal)) is { } viewRole
                                 && _perPlayerProviderByMember.TryGetValue(segments[1], out CatalogProvider? roleProvider))
            {
                entity = new EntityProviderReference(
                    reference.Path, roleProvider.Name, segments[0], viewRole.Field);
                return true;
            }

            entity = null;
            return false;
        }

        // ── Mapping helpers ────────────────────────────────────────────────────────

        private static RuleNodeKind MapNodeKind(StatKind kind) =>
            kind switch
            {
                StatKind.Flag => RuleNodeKind.Flag,
                StatKind.Count => RuleNodeKind.Count,
                StatKind.Sum => RuleNodeKind.Sum,
                StatKind.Capture => RuleNodeKind.Capture,
                StatKind.Compute => RuleNodeKind.Compute,
                StatKind.Tally => RuleNodeKind.Tally,
                StatKind.Streak => RuleNodeKind.Streak,
                StatKind.Bucket => RuleNodeKind.Bucket,
                StatKind.Rate => RuleNodeKind.Rate,
                StatKind.Burst => RuleNodeKind.Burst,
                _ => RuleNodeKind.None
            };

        private static KeepKind MapKeep(StatDef stat)
        {
            if (stat.Kind != StatKind.Capture)
            {
                return KeepKind.None;
            }

            return stat.Keep switch
            {
                KeepMode.First => KeepKind.First,
                KeepMode.List => KeepKind.List,
                KeepMode.Min => KeepKind.Min,
                KeepMode.Max => KeepKind.Max,
                _ => KeepKind.Last // capture default is last-value semantics
            };
        }

        private static ScopeAxis ComputeScope(PerScope per, bool forEachPlayer) =>
            (forEachPlayer, per) switch
            {
                (true, PerScope.Match) => ScopeAxis.PlayerMatch,
                (true, _) => ScopeAxis.PlayerRound,
                (false, PerScope.Match) => ScopeAxis.Match,
                _ => ScopeAxis.Round
            };

        private static string LastSegment(string path)
        {
            int dot = path.LastIndexOf('.');
            return dot < 0 ? path : path[(dot + 1)..];
        }

        // ── Diagnostics ────────────────────────────────────────────────────────────

        private void Report(string code, string message, SourcePosition pos) =>
            _diagnostics.Add(new RulesetDiagnostic(code, message, pos));

        private void ReportCore(IReadOnlyList<Diagnostic> coreDiagnostics, string slot, SourcePosition pos)
        {
            foreach (Diagnostic diagnostic in coreDiagnostics)
            {
                string offending = string.IsNullOrEmpty(diagnostic.OffendingText)
                    ? ""
                    : $" ('{diagnostic.OffendingText}')";
                _diagnostics.Add(new RulesetDiagnostic(diagnostic.Code,
                    $"{slot} {diagnostic.Message}{offending}", pos));
            }
        }

        // ── Read-set / entity-provider extraction ──────────────────────────────────

        private sealed class ReadCollector(Session session, CatalogView? view)
        {
            private readonly List<EntityProviderReference> _entities = [];
            private readonly HashSet<string> _entitySeen = new(StringComparer.Ordinal);
            private readonly List<string> _reads = [];
            private readonly HashSet<string> _seen = new(StringComparer.Ordinal);

            internal IReadOnlyList<string> DeclaredReads => _reads;

            internal IReadOnlyList<EntityProviderReference> EntityReads => _entities;

            internal void Collect(CheckedExpression? expression)
            {
                if (expression is null)
                {
                    return;
                }

                foreach (ResolvedReference reference in expression.References)
                {
                    if (_seen.Add(reference.Path))
                    {
                        _reads.Add(reference.Path);
                    }

                    if (session.TryEntityRead(reference, view, out EntityProviderReference? entity)
                        && _entitySeen.Add(entity.Path))
                    {
                        _entities.Add(entity);
                    }
                }
            }
        }

        /// <summary>Mutable accumulator for a stat's resolved trigger across the splice + refinement merge.</summary>
        private sealed class ResolvedTrigger
        {
            internal bool Ok { get; set; }

            internal CatalogView? View { get; set; }

            internal string? RawOrNetName { get; set; }

            internal bool IsNet { get; set; }

            internal string? FlagSource { get; set; }

            internal HashSet<string> MatchKeys { get; } = new(StringComparer.Ordinal);

            internal List<MatchBinding> PendingMatch { get; } = [];

            internal IReadOnlyList<(CatalogFacet Facet, UnaryTest Test)> MergedMatch { get; set; } = [];

            /// <summary>Net-trigger field-facet match bindings, lowered to <c>event.&lt;Field&gt;</c> where-conjuncts.</summary>
            internal List<MatchBinding> NetMatch { get; } = [];

            internal string? DefineWhere { get; set; }

            internal string? SiteWhere { get; set; }

            internal List<string> Whiles { get; } = [];

            internal bool ActorAny { get; set; }

            /// <summary>True when the trigger fires on an event/flag source (view, raw, net, or a sibling flag).</summary>
            internal bool HasEventSource => View is not null || RawOrNetName is not null || FlagSource is not null;

            internal IReadOnlyList<string> RoleNames =>
                View?.Roles.Select(r => r.Role).ToArray() ?? [];
        }
    }
}
