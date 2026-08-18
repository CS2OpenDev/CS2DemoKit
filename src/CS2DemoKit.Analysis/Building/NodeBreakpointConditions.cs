#region

using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Compiles and evaluates conditional <em>node</em> breakpoints against the captured per-message
///     <see cref="NodeSnapshot" /> rows — no graph re-evaluation. A condition may reference:
///     <list type="bullet">
///         <item>the target node's own numeric <c>value</c>;</item>
///         <item>
///             any other <b>tracked</b> node by name — its numeric value, or (for a bool node) its
///             active state, since <c>BoolNode.Value</c> <em>is</em> the bool;
///         </item>
///         <item>
///             game-wide entity contexts such as <c>entity.game.freeze_period</c> — these are tracked
///             value nodes too (their <c>Name</c> is the provider's <c>ContextName</c>), so their
///             per-message value is already in the snapshot.
///         </item>
///     </list>
///     The mechanism: bind a typed <em>proxy</em> per tracked node into the existing
///     <see cref="ExpressionCompiler.CompileNodeExpression" /> resolution (which reads each name's
///     <c>.Value</c>), then drive the proxies from the snapshot one message at a time. The same proxy
///     universe is used for validation and evaluation, so a condition that validates can never then
///     fail to resolve at scan time (which would silently yield zero hits).
///     <para>
///         <b>"No value never matches":</b> a numeric reference with no value this message is fed
///         <see cref="double.NaN" />; every comparison against NaN is <c>false</c>, so a value
///         condition on a node that has no numeric value (a bool node, or an inactive counter) simply
///         doesn't match — rather than matching against a fabricated 0.
///     </para>
/// </summary>
public static class NodeBreakpointConditions
{
    /// <summary>The snapshot lane a tracked node's value is read from.</summary>
    public enum ValueKind
    {
        /// <summary>Non-numeric, non-bool, non-text (enum etc.) — not referenceable in a condition.</summary>
        None,

        /// <summary>Bool node — read from <see cref="NodeSnapshot.IsActive" />.</summary>
        Bool,

        /// <summary>Numeric value node — read from <see cref="NodeSnapshot.NumericValue" /> (NaN when absent).</summary>
        Number,

        /// <summary>String value node — read from <see cref="NodeSnapshot.DisplayValue" />.</summary>
        Text
    }

    // Matches an `input.<event>` reference (optional spaces around the dot) and captures the event name.
    private static readonly Regex _inputRefPattern =
        new(@"\binput\s*\.\s*([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Classifies a tracked node so its proxy exposes a correctly-typed <c>.Value</c>.</summary>
    public static ValueKind Classify(StateNode node)
    {
        if (node is BoolNode)
        {
            return ValueKind.Bool;
        }

        Type? t = node.GetType();
        while (t is not null && !(t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ValueNode<>)))
        {
            t = t.BaseType;
        }

        if (t is null)
        {
            return ValueKind.None;
        }

        Type arg = t.GetGenericArguments()[0];
        if (arg == typeof(bool))
        {
            return ValueKind.Bool;
        }

        if (arg == typeof(string))
        {
            return ValueKind.Text;
        }

        if (arg == typeof(int) || arg == typeof(long) || arg == typeof(double) || arg == typeof(float)
            || arg == typeof(short) || arg == typeof(byte) || arg == typeof(uint) || arg == typeof(ushort))
        {
            return ValueKind.Number;
        }

        return ValueKind.None;
    }

    // The distinct input-event names a condition references (e.g. ["player_death"]). Empty for a pure
    // state condition. String literals are stripped first so an "input.x" inside a quoted value (a
    // valid pure-state text comparison) isn't mistaken for an input reference. The lexer has no string
    // escapes, so removing `"[^"]*"` is exact.
    private static List<string> ExtractInputEventNames(string condition)
    {
        string scannable = Regex.Replace(condition, "\"[^\"]*\"", "");
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in _inputRefPattern.Matches(scannable))
        {
            names.Add(m.Groups[1].Value);
        }

        return names.ToList();
    }

    /// <summary>
    ///     Validates a condition against the tracked-node universe (and, when it references
    ///     <c>input.&lt;event&gt;</c>, that node's input events). Returns an error message, or
    ///     <c>null</c> when valid (blank / <c>"active"</c> are valid). Shares the exact compile path
    ///     (and the same kind-typed <c>value</c> proxy) used by <see cref="ComputeHits" />.
    ///     <para>
    ///         <paramref name="targetColumn" /> is the breakpoint's own node column: it types the
    ///         <c>value</c> keyword to that node's kind. <paramref name="inputEvents" /> are the node's
    ///         direct input events (event name → info); a condition may reference one of them via
    ///         <c>input.&lt;event&gt;.&lt;field&gt;</c>. Two distinct input events, or one with no
    ///         matching input edge, are rejected.
    ///     </para>
    /// </summary>
    public static string? Validate(
        string? condition,
        IReadOnlyList<StateNode> trackedByColumn,
        int targetColumn,
        IReadOnlyDictionary<string, InputEventInfo>? inputEvents = null,
        int? selectedPlayerSlot = null,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders = null)
    {
        if (IsDefault(condition))
        {
            return null;
        }

        try
        {
            (Dictionary<string, object> universe, _) = BuildUniverse(trackedByColumn);
            (object valueProxy, _) = ValueProxyFor(trackedByColumn, targetColumn);
            universe["value"] = valueProxy; // the target's own value, typed to its kind

            List<string> inputNames = ExtractInputEventNames(condition!);
            if (inputNames.Count == 0)
            {
                ExpressionCompiler.CompileNodeExpression(condition!, universe);
                return null;
            }

            if (inputNames.Count > 1)
            {
                return "A condition may reference only one input event (only one event fires per message).";
            }

            string ev = inputNames[0];
            if (inputEvents is null || !inputEvents.TryGetValue(ev, out InputEventInfo? info))
            {
                return $"This node has no direct input edge for event '{ev}'.";
            }

            ExpressionCompiler.CompileNodeMixedExpression(
                condition!, universe, ev, info.EventType, info.Fields, selectedPlayerSlot,
                perPlayerProviders, info.ParameterType);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>
    ///     Computes the sorted message indices a node breakpoint's condition matches, dispatching on
    ///     the condition's <em>substrate</em>:
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Pure state</b> (no <c>input.*</c>): the rising edge of the predicate over the
    ///             per-message snapshot (<c>cond(i) &amp;&amp; !cond(i-1)</c>). Blank / <c>"active"</c>
    ///             rises with the target's own active state.
    ///         </item>
    ///         <item>
    ///             <b>Input-event</b> (references <c>input.&lt;event&gt;</c>): <em>discrete</em> over the
    ///             event's fire indices — each fire whose payload (and any joined snapshot state)
    ///             satisfies the predicate is a hit. The joined state is read <em>pre-event</em> (the
    ///             snapshot row before the fire), so this is the "stop when the node was in state X
    ///             <em>just before</em> this input event fired with fields Y" capability — it still
    ///             holds for a node the event itself deactivates.
    ///         </item>
    ///     </list>
    ///     Returns an empty list for an invalid condition (validation blocks saving those) or an
    ///     out-of-range target. <paramref name="inputEvents" /> / <paramref name="payloadAt" /> are
    ///     required only for the input-event substrate.
    /// </summary>
    public static List<int> ComputeHits(
        SnapshotTable snapshots,
        IReadOnlyList<StateNode> trackedByColumn,
        int targetColumn,
        string? condition,
        IReadOnlyDictionary<string, InputEventInfo>? inputEvents = null,
        Func<int, object?>? payloadAt = null,
        int? selectedPlayerSlot = null) =>
        // Synchronous entry (no entity providers → entity-read conditions can't compile here and yield no
        // sync hits). Hosts that support entity reads call PlanNodeHits, which can defer to a cache.
        PlanNodeHits(snapshots, trackedByColumn, targetColumn, condition, inputEvents, payloadAt,
            selectedPlayerSlot, null).SyncHits ?? [];

    /// <summary>
    ///     Plans a node breakpoint's hits, dispatching by substrate exactly as <see cref="ComputeHits" />
    ///     describes, but routing the input-event substrate through a reusable matcher so an
    ///     <em>entity-read</em> condition returns a DEFERRED plan (the host builds the entity cache, then
    ///     calls <see cref="NodeHitPlan.Recompute" />) instead of computing inline. Pure-state / pure-input /
    ///     bare-player conditions resolve synchronously. A negative selected slot short-circuits a
    ///     <c>player</c>-referencing condition to no hits.
    /// </summary>
    public static NodeHitPlan PlanNodeHits(
        SnapshotTable snapshots,
        IReadOnlyList<StateNode> trackedByColumn,
        int targetColumn,
        string? condition,
        IReadOnlyDictionary<string, InputEventInfo>? inputEvents,
        Func<int, object?>? payloadAt,
        int? selectedPlayerSlot,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders)
    {
        if (IsDefault(condition))
        {
            return NodeHitPlan.Sync(RisingEdge(snapshots, i => targetColumn >= 0
                                                               && targetColumn < snapshots.Width
                                                               && snapshots[i, targetColumn].IsActive));
        }

        List<string> inputNames = ExtractInputEventNames(condition!);
        if (inputNames.Count == 0)
        {
            return NodeHitPlan.Sync(ComputeStateHits(snapshots, trackedByColumn, targetColumn, condition!));
        }

        if (inputNames.Count > 1)
        {
            return NodeHitPlan.Sync([]); // >1 distinct input event — unsatisfiable (validation rejects)
        }

        NodeInputMatcher? matcher = TryBuildInputMatcher(
            snapshots, trackedByColumn, targetColumn, condition!, inputNames[0], inputEvents, payloadAt,
            selectedPlayerSlot, perPlayerProviders);
        if (matcher is null)
        {
            return NodeHitPlan.Sync([]); // no matching input edge / invalid condition (validation rejects)
        }

        // References the selected player but none is selected → inert (a negative slot's bare-`player`
        // comparison / entity coalesce-to-default would otherwise match every fire). Mirrors the edge path.
        if (matcher.ReferencesSelectedPlayer && selectedPlayerSlot is < 0)
        {
            return NodeHitPlan.Sync([]);
        }

        return matcher.NeedsEntityCache
            ? NodeHitPlan.Deferred(matcher.FireMessageIndices, matcher.Compute)
            : NodeHitPlan.Sync(matcher.Compute(null));
    }

    // Pure-state substrate: rising edge of the predicate over the snapshot columns.
    private static List<int> ComputeStateHits(
        SnapshotTable snapshots, IReadOnlyList<StateNode> trackedByColumn, int targetColumn, string condition)
    {
        (Dictionary<string, object> universe, Dictionary<string, (object, int, ValueKind)> feed) =
            BuildUniverse(trackedByColumn);
        BindValueProxy(universe, feed, trackedByColumn, targetColumn);

        RecordingLookup recording = new(universe);
        Func<double> compiled;
        try
        {
            compiled = ExpressionCompiler.CompileNodeExpression(condition, recording);
        }
        catch
        {
            return [];
        }

        (object Proxy, int Column, ValueKind Kind)[] feeders = Feeders(recording, feed);
        return RisingEdge(snapshots, i =>
        {
            FeedProxies(feeders, snapshots, i);
            return compiled() != 0.0;
        });
    }

    // Builds a reusable matcher for the input-event substrate: compiles the predicate once and captures
    // the state proxies it reads, so the hits can be (re)computed against a positioned entity accessor
    // without recompiling. Returns null when there's no matching input edge or the condition won't compile
    // (validation rejects both). The slot<0 short-circuit and the sync-vs-deferred routing live in the
    // caller (PlanNodeHits), which can read the matcher's NeedsEntityCache / ReferencesSelectedPlayer flags.
    private static NodeInputMatcher? TryBuildInputMatcher(
        SnapshotTable snapshots, IReadOnlyList<StateNode> trackedByColumn, int targetColumn,
        string condition, string eventName,
        IReadOnlyDictionary<string, InputEventInfo>? inputEvents, Func<int, object?>? payloadAt,
        int? selectedPlayerSlot, PerPlayerEntityValueProviderRegistry? perPlayerProviders)
    {
        if (inputEvents is null || payloadAt is null || !inputEvents.TryGetValue(eventName, out InputEventInfo? info))
        {
            return null; // no such direct input edge (validation rejects)
        }

        (Dictionary<string, object> universe, Dictionary<string, (object, int, ValueKind)> feed) =
            BuildUniverse(trackedByColumn);
        BindValueProxy(universe, feed, trackedByColumn, targetColumn);

        RecordingLookup recording = new(universe);
        NodeMixedCompileResult result;
        try
        {
            result = ExpressionCompiler.CompileNodeMixedExpression(
                condition, recording, eventName, info.EventType, info.Fields, selectedPlayerSlot,
                perPlayerProviders, info.ParameterType);
        }
        catch
        {
            return null;
        }

        (object Proxy, int Column, ValueKind Kind)[] feeders = Feeders(recording, feed);
        return new NodeInputMatcher(snapshots, result.Predicate, feeders, info.FireIndices, payloadAt,
            result.NeedsEntityCache, result.ReferencesSelectedPlayer);
    }

    // Binds the target's own `value` proxy (typed to its kind) into the universe + feed.
    private static void BindValueProxy(
        Dictionary<string, object> universe,
        Dictionary<string, (object, int, ValueKind)> feed,
        IReadOnlyList<StateNode> trackedByColumn,
        int targetColumn)
    {
        (object valueProxy, ValueKind valueKind) = ValueProxyFor(trackedByColumn, targetColumn);
        universe["value"] = valueProxy;
        feed["value"] = (valueProxy, targetColumn, valueKind);
    }

    // The proxies the compiled expression actually resolved (the compiler's own resolution set, ⊆ the
    // validated universe) — fed each message so the predicate reads live snapshot state.
    private static (object Proxy, int Column, ValueKind Kind)[] Feeders(
        RecordingLookup recording, Dictionary<string, (object, int, ValueKind)> feed) =>
        recording.Resolved
            .Where(feed.ContainsKey)
            .Select(k => feed[k])
            .ToArray();

    // Sets each resolved proxy's value from the snapshot row (NaN / inactive / null when out of
    // range, or when rowIndex < 0 — the "no prior row" baseline).
    private static void FeedProxies(
        (object Proxy, int Column, ValueKind Kind)[] feeders, SnapshotTable snapshots, int rowIndex)
    {
        foreach ((object proxy, int col, ValueKind kind) in feeders)
        {
            bool inRange = rowIndex >= 0 && col >= 0 && col < snapshots.Width;
            NodeSnapshot cell = inRange ? snapshots[rowIndex, col] : default;
            switch (kind)
            {
                case ValueKind.Number:
                    ((NumberProxy)proxy).Value = inRange ? cell.NumericValue ?? double.NaN : double.NaN;
                    break;
                case ValueKind.Bool:
                    ((BoolProxy)proxy).Value = inRange && cell.IsActive;
                    break;
                case ValueKind.Text:
                    ((TextProxy)proxy).Value = inRange ? cell.DisplayValue : null;
                    break;
            }
        }
    }

    /// <summary>
    ///     The identifiers a condition may reference, for editor autocomplete: every referenceable
    ///     tracked node's name (bool / number / text — entity contexts included, since their Name is
    ///     the dotted ContextName) plus the <c>value</c> / <c>active</c> keywords. Sorted, distinct.
    ///     Sourced from the same tracked universe validation and scanning use.
    /// </summary>
    public static IReadOnlyList<string> AvailableIdentifiers(IReadOnlyList<StateNode> trackedByColumn)
    {
        SortedSet<string> ids = new(StringComparer.Ordinal)
        {
            "value",
            "active"
        };
        foreach (StateNode node in trackedByColumn)
        {
            if (Classify(node) != ValueKind.None)
            {
                ids.Add(node.Name);
            }
        }

        return ids.ToList();
    }

    /// <summary>
    ///     The <c>input.&lt;event&gt;.&lt;field&gt;</c> identifiers a node's condition may reference, for
    ///     editor autocomplete — one per field of each direct input event. Sorted, distinct.
    /// </summary>
    public static IReadOnlyList<string> InputFieldIdentifiers(IReadOnlyDictionary<string, InputEventInfo> inputEvents) =>
        InputFieldIdentifiers(inputEvents, null);

    /// <summary>
    ///     The autocomplete identifiers for a node's input-event condition, including the per-player /
    ///     entity grammar when <paramref name="perPlayerProviders" /> is supplied: <c>player</c> (the
    ///     selected slot), <c>player.&lt;provider&gt;</c> (the selected player's entity), and — note the
    ///     <c>input.&lt;event&gt;.</c> prefix, unlike the edge form —
    ///     <c>
    ///         input.&lt;event&gt;.&lt;SlotField&gt;.
    ///         &lt;provider&gt;
    ///     </c>
    ///     for the event-subject read. Pass <paramref name="includeEntityReads" /> =
    ///     <c>false</c> to keep the bare <c>player</c> slot-comparison token but drop the entity-read
    ///     grammar (the scope-aware editor authors entity reads through structured rows, so the free-text
    ///     event-match box no longer suggests them). Every identifier round-trips through
    ///     <see cref="Validate" /> (the picker never suggests an unparseable string). Sorted, distinct.
    /// </summary>
    public static IReadOnlyList<string> InputFieldIdentifiers(
        IReadOnlyDictionary<string, InputEventInfo> inputEvents,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders,
        bool includeEntityReads = true)
    {
        SortedSet<string> ids = new(StringComparer.Ordinal);
        foreach ((string ev, InputEventInfo info) in inputEvents)
        {
            foreach (string field in info.Fields.Keys)
            {
                ids.Add($"input.{ev}.{field}");
            }
        }

        if (perPlayerProviders is not null)
        {
            ids.Add("player"); // bare slot-comparison subject: input.<ev>.<Slot> == player

            if (includeEntityReads)
            {
                List<string> providerNames = perPlayerProviders.All.Select(p => p.Name).ToList();
                foreach (string provider in providerNames)
                {
                    ids.Add($"player.{provider}"); // player.entity.pawn.health
                }

                foreach ((string ev, InputEventInfo info) in inputEvents)
                {
                    foreach (string slotField in info.Fields
                                 .Where(kv => ExpressionCompiler.IsPlayerSlotField(kv.Key, kv.Value.FieldType))
                                 .Select(kv => kv.Key))
                    {
                        foreach (string provider in providerNames)
                        {
                            ids.Add($"input.{ev}.{slotField}.{provider}"); // input.player_death.UserId.entity.pawn.health
                        }
                    }
                }
            }
        }

        return ids.ToList();
    }

    /// <summary>
    ///     Builds a condition snippet for a node picked from the graph, seeded with its <em>current</em>
    ///     state: <c>name == true|false</c> for a bool (or entity-context bool), <c>name == &lt;n&gt;</c>
    ///     for a numeric node that has a value, <c>name == "text"</c> for a string node, and a bare
    ///     <c>name</c> when there's no current value to embed (an inactive counter — don't fabricate
    ///     <c>== 0</c>). The result is guaranteed to parse against a universe containing that node
    ///     (see the round-trip test), so the picker never inserts something that immediately errors.
    /// </summary>
    public static string SuggestPickSnippet(string nodeName, ValueKind kind, bool isActive, double? numericValue, string? displayValue)
    {
        switch (kind)
        {
            case ValueKind.Bool:
                return $"{nodeName} == {(isActive ? "true" : "false")}";
            case ValueKind.Number:
                return numericValue is { } v && !double.IsNaN(v)
                    ? $"{nodeName} == {FormatNumber(v)}"
                    : nodeName;
            case ValueKind.Text:
                return displayValue is not null
                    ? $"{nodeName} == \"{displayValue}\""
                    : nodeName;
            default:
                return nodeName;
        }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    // The `value` keyword binds to a proxy typed to the TARGET node's kind, so a condition on the
    // target's own value type-checks correctly: `value == true` on a bool node, `value >= 3` on a
    // numeric node, `value == "x"` on a text node. Number is the fallback for an out-of-range target
    // or a kind with no value lane (None) — harmless, since such a node is rarely a value-condition
    // target. Validate and ComputeHits both route through here so their `value` proxy stays identical.
    private static (object Proxy, ValueKind Kind) ValueProxyFor(IReadOnlyList<StateNode> trackedByColumn, int targetColumn)
    {
        ValueKind kind = targetColumn >= 0 && targetColumn < trackedByColumn.Count
            ? Classify(trackedByColumn[targetColumn])
            : ValueKind.Number;

        if (kind == ValueKind.None)
        {
            kind = ValueKind.Number;
        }

        object proxy = kind switch
        {
            ValueKind.Bool => new BoolProxy(),
            ValueKind.Text => new TextProxy(),
            _ => new NumberProxy()
        };

        return (proxy, kind);
    }

    private static string FormatNumber(double v) =>
        v == Math.Floor(v) && !double.IsInfinity(v)
            ? ((long)v).ToString(CultureInfo.InvariantCulture)
            : v.ToString(CultureInfo.InvariantCulture);

    private static bool IsDefault(string? condition) =>
        string.IsNullOrWhiteSpace(condition) || condition.Trim() == "active";

    private static List<int> RisingEdge(SnapshotTable snapshots, Func<int, bool> condition)
    {
        List<int> hits = [];
        bool prev = false;
        for (int i = 0; i < snapshots.Count; i++)
        {
            bool cur = condition(i);
            if (cur && !prev)
            {
                hits.Add(i);
            }

            prev = cur;
        }

        return hits;
    }

    // Builds a typed proxy per tracked node, keyed by node Name (== entity ContextName for entity
    // contexts). The proxy's `.Value` type matches the node's value type so the compiler type-checks
    // comparisons (e.g. `boolNode >= 3` fails to compile → validation rejects it).
    private static (Dictionary<string, object> Universe, Dictionary<string, (object, int, ValueKind)> Feed)
        BuildUniverse(IReadOnlyList<StateNode> trackedByColumn)
    {
        Dictionary<string, object> universe = new(StringComparer.Ordinal);
        Dictionary<string, (object, int, ValueKind)> feed = new(StringComparer.Ordinal);

        for (int col = 0; col < trackedByColumn.Count; col++)
        {
            StateNode node = trackedByColumn[col];
            ValueKind kind = Classify(node);
            object? proxy = kind switch
            {
                ValueKind.Bool => new BoolProxy(),
                ValueKind.Number => new NumberProxy(),
                ValueKind.Text => new TextProxy(),
                _ => null
            };

            if (proxy is null)
            {
                continue;
            }

            // Last writer wins on a duplicate name (names are unique in practice).
            universe[node.Name] = proxy;
            feed[node.Name] = (proxy, col, kind);
        }

        return (universe, feed);
    }

    /// <summary>
    ///     The event that activates a node (its direct input edge), exposed for an
    ///     <c>input.&lt;event&gt;.&lt;field&gt;</c> condition: the CLR event type, its accessible fields,
    ///     and the sorted message indices the edge fired at (the candidate set for a node-input
    ///     condition — the union across the node's direct input edges of that event).
    ///     <see cref="ParameterType" /> is the compiled predicate's parameter when it differs from
    ///     <see cref="EventType" /> — the <c>GameEvent</c> envelope for a game event, so per-fire
    ///     transport (<c>input.&lt;event&gt;.tick</c>) resolves; <c>null</c> for a net message, whose
    ///     payload is itself the subject.
    /// </summary>
    public sealed record InputEventInfo(
        Type EventType,
        IReadOnlyDictionary<string, EventFieldAccessor> Fields,
        IReadOnlyList<int> FireIndices,
        Type? ParameterType = null);

    /// <summary>
    ///     The disposition of a node breakpoint's hit computation: either resolved <see cref="SyncHits" />
    ///     (default / pure-state / pure-input / bare-player — no entity replay) or a <em>deferred</em> plan
    ///     (<see cref="NeedsEntityCache" />) the host fulfils by building an entity cache over
    ///     <see cref="FireMessageIndices" /> then calling <see cref="Recompute" /> with a per-fire accessor.
    /// </summary>
    public sealed class NodeHitPlan
    {
        private NodeHitPlan(
            List<int>? syncHits, bool needsEntityCache, IReadOnlyList<int> fireMessageIndices,
            Func<Func<int, IEntityValueAt?>?, List<int>>? recompute)
        {
            SyncHits = syncHits;
            NeedsEntityCache = needsEntityCache;
            FireMessageIndices = fireMessageIndices;
            Recompute = recompute;
        }

        /// <summary>The computed hits when no entity replay is needed; <c>null</c> for a deferred plan.</summary>
        public List<int>? SyncHits { get; }

        /// <summary>The condition reads a per-player entity provider → use the deferred cache-backed path.</summary>
        public bool NeedsEntityCache { get; }

        /// <summary>The event's fire message indices (the frames the entity cache must cover).</summary>
        public IReadOnlyList<int> FireMessageIndices { get; }

        /// <summary>
        ///     Re-runs the predicate with a positioned entity accessor (<c>frameIndexOfMessage → accessor</c>),
        ///     feeding pre-event node state and pre-frame entity state at each fire. <c>null</c> for a sync plan.
        /// </summary>
        public Func<Func<int, IEntityValueAt?>?, List<int>>? Recompute { get; }

        internal static NodeHitPlan Sync(List<int> hits) => new(hits, false, [], null);

        internal static NodeHitPlan Deferred(
            IReadOnlyList<int> fireMessageIndices, Func<Func<int, IEntityValueAt?>?, List<int>> recompute) =>
            new(null, true, fireMessageIndices, recompute);
    }

    // A compiled node input-event predicate (Func&lt;TEvent, IEntityValueAt, double&gt;) plus the state
    // proxies it reads — so its hits can be (re)computed against a positioned entity accessor without
    // recompiling (a condition edit / selection change re-filters the same cache). Discrete over the
    // event's fire indices, feeding the PRE-EVENT snapshot state (the row before the fire) AND the event
    // payload AND a pre-frame entity accessor at each fire so a mixed
    // `value … && input.<event>.<field> …` (or `… VictimSlot.entity.pawn.health < 20`) intersects all of
    // them at the SAME message — what independent breakpoints (which union across all messages) can't.
    private sealed class NodeInputMatcher(
        SnapshotTable snapshots,
        Delegate compiled,
        (object Proxy, int Column, ValueKind Kind)[] feeders,
        IReadOnlyList<int> fireIndices,
        Func<int, object?> payloadAt,
        bool needsEntityCache,
        bool referencesSelectedPlayer)
    {
        public bool NeedsEntityCache => needsEntityCache;
        public bool ReferencesSelectedPlayer => referencesSelectedPlayer;
        public IReadOnlyList<int> FireMessageIndices => fireIndices;

        // Computes the matching fire indices. accessorAt positions the entity accessor at each fire's
        // PRE-FRAME state; null = no cache (used for non-entity conditions, whose predicate never reads
        // it). For an entity condition, a null accessor value at a fire → non-match (parity with the edge
        // path's FilterAppliedWithEntities). The state proxies are always fed PRE-EVENT (snapshots[i-1]).
        public List<int> Compute(Func<int, IEntityValueAt?>? accessorAt)
        {
            if (needsEntityCache && accessorAt is null)
            {
                return []; // entity reads with no positioned cache → nothing (defensive; host supplies one)
            }

            List<int> hits = [];
            foreach (int i in fireIndices)
            {
                if (i < 0 || i >= snapshots.Count)
                {
                    continue;
                }

                // PRE-EVENT state: the row from BEFORE message i's edges applied. snapshots[i] is captured
                // AFTER message i, so snapshots[i-1] is the state as the event arrived — what makes a mixed
                // condition work on a node the event DEACTIVATES (NoDeathsYet + `value == true && input…`):
                // at the fatal kill the node is still active in the pre-event row though snapshots[i] shows
                // it inactive. A fire at message 0 has no prior row → all-inactive/NaN baseline. NOTE: this
                // pre-event STATE read (message-granular) and the pre-frame ENTITY read below (frame-
                // granular) land at different instants when the fire isn't the first message in its frame —
                // by design: each clause reads at its own correct pre-point.
                FeedProxies(feeders, snapshots, i > 0 ? i - 1 : -1);

                object? payload = payloadAt(i);
                if (payload is null)
                {
                    continue;
                }

                IEntityValueAt accessor;
                if (needsEntityCache)
                {
                    IEntityValueAt? positioned = accessorAt!(i);
                    if (positioned is null)
                    {
                        continue; // pre-frame entity state unavailable at this fire → non-match
                    }

                    accessor = positioned;
                }
                else
                {
                    accessor = NoopEntityValueAt.Instance;
                }

                try
                {
                    // Validated at edit time, so a data-dependent runtime throw (e.g. ÷0) → non-match.
                    if (compiled.DynamicInvoke(payload, accessor) is double d && d != 0.0)
                    {
                        hits.Add(i);
                    }
                }
                catch
                {
                    // non-match
                }
            }

            return hits;
        }
    }

    // The compiler reads each bound name's `.Value`; these expose a strongly-typed one so the
    // expression's type-checking matches the node's real value type.
    private sealed class NumberProxy
    {
        public double Value { get; set; }
    }

    private sealed class BoolProxy
    {
        public bool Value { get; set; }
    }

    private sealed class TextProxy
    {
        public string? Value { get; set; }
    }

    // Wraps the proxy universe and records every key the compiler successfully resolves, so the
    // scan feeds exactly the proxies the expression reads — never more, never (the silent-failure
    // case) fewer than were available at validation time.
    private sealed class RecordingLookup(IReadOnlyDictionary<string, object> inner) : IReadOnlyDictionary<string, object>
    {
        public HashSet<string> Resolved { get; } = new(StringComparer.Ordinal);

        public bool TryGetValue(string key, out object value)
        {
            bool found = inner.TryGetValue(key, out value!);
            if (found)
            {
                Resolved.Add(key);
            }

            return found;
        }

        public object this[string key]
        {
            get
            {
                Resolved.Add(key);
                return inner[key];
            }
        }

        public bool ContainsKey(string key) => inner.ContainsKey(key);
        public IEnumerable<string> Keys => inner.Keys;
        public IEnumerable<object> Values => inner.Values;
        public int Count => inner.Count;
        public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => inner.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => inner.GetEnumerator();
    }
}
