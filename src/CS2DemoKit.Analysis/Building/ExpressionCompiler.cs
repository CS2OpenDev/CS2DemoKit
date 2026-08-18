#region

using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Parser.GameEvents;

#endregion

#pragma warning disable CA2263 // runtime-determined type parameters require non-generic Lambda

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     The set of identifiers an <see cref="ExpressionCompiler" /> call binds when parsing a YAML
///     expression. Anything not set in the bindings is treated as an unbound symbol and rejected.
/// </summary>
public sealed class ExpressionBindings
{
    /// <summary>Map of enrichment-context names to their backing nodes (resolved as <c>enrich.xxx</c> identifiers).</summary>
    public IReadOnlyDictionary<string, object>? EnrichmentNodes { get; init; }

    /// <summary>Field accessors keyed by name for an event-scoped expression (resolved as <c>e.fieldName</c>).</summary>
    public IReadOnlyDictionary<string, EventFieldAccessor>? EventFields { get; init; }

    /// <summary>Parameter expression bound to the event payload (the <c>e</c> in <c>e.fieldName</c>).</summary>
    public ParameterExpression? EventParam { get; init; }

    /// <summary>
    ///     The type whose properties <c>event.&lt;field&gt;</c> names. Usually the same as
    ///     <see cref="EventParam" />'s type; for a game event they differ — the parameter is the
    ///     <see cref="GameEvent" /> envelope so per-fire transport stays addressable, while the wire
    ///     fields live on the payload record this names. <c>null</c> means "the parameter's own type".
    /// </summary>
    public Type? EventPayloadType { get; init; }

    /// <summary>
    ///     A node's <em>input event</em> binding, resolving <c>input.&lt;event&gt;.&lt;field&gt;</c> against
    ///     the payload of the event that activates the node. <c>EventName</c> is the one event the
    ///     condition may reference (a node-input condition is scoped to a single event type — only one
    ///     event fires per message); <c>Param</c> is bound to that payload; <c>Fields</c> are its
    ///     accessible fields. Set only for node mixed-substrate conditions; <c>null</c> otherwise.
    /// </summary>
    public (string EventName, ParameterExpression Param, IReadOnlyDictionary<string, EventFieldAccessor> Fields)?
        InputEvent { get; init; }

    /// <summary>
    ///     The payload type behind <see cref="InputEvent" />'s parameter — the <see cref="EventPayloadType" />
    ///     counterpart for the <c>input.&lt;event&gt;.&lt;field&gt;</c> form. <c>null</c> means the
    ///     parameter is itself the subject.
    /// </summary>
    public Type? InputEventPayloadType { get; init; }

    /// <summary>Expression giving the current rule's stored value (resolved as <c>node.value</c>).</summary>
    public Expression? NodeValue { get; init; }

    /// <summary>
    ///     If set, <c>player.team</c> resolves to a runtime call into this index,
    ///     reflecting halftime side swaps. Otherwise it falls back to <see cref="PlayerTeam" />.
    /// </summary>
    public PlayerContextIndex? PlayerContextIndex { get; init; }

    /// <summary>Player name for per-player chains (resolved as <c>player.name</c>).</summary>
    public string? PlayerName { get; init; }

    /// <summary>Player slot for per-player chains (resolved as <c>player.slot</c>).</summary>
    public int? PlayerSlot { get; init; }

    /// <summary>Initial team for per-player chains; used as the fallback when no <see cref="PlayerContextIndex" /> is bound.</summary>
    public int? PlayerTeam { get; init; }

    /// <summary>Parameter expression bound to a value-conditional predicate's input (the <c>v</c> in <c>v == 5</c>).</summary>
    public ParameterExpression? ValueParam { get; init; }

    /// <summary>
    ///     Scanner whose per-frame snapshot backs <c>player.entity.*</c> reads. Required (together with
    ///     <see cref="PerPlayerProviders" /> and <see cref="PlayerSlot" />) to resolve a
    ///     <c>player.entity.&lt;provider-name&gt;</c> reference; absent → such a reference is a compile error.
    /// </summary>
    public EntityChangeScanner? EntityScanner { get; init; }

    /// <summary>
    ///     Per-player entity-value providers consulted when resolving <c>player.entity.*</c>. The dotted path
    ///     after <c>player.</c> is the provider's registered <see cref="IPerPlayerEntityValueProvider.Name" />
    ///     (e.g. <c>player.entity.pawn.health</c> → provider <c>entity.pawn.health</c>).
    /// </summary>
    public PerPlayerEntityValueProviderRegistry? PerPlayerProviders { get; init; }

    /// <summary>
    ///     The filter's selected player slot, bound for a bare <c>player</c> reference in an EDGE
    ///     breakpoint condition (e.g. <c>event.Attacker == player</c>). Resolves to a compile-time int
    ///     constant; the host recompiles the predicate when the selection changes. <c>null</c> outside the
    ///     edge breakpoint path.
    /// </summary>
    public int? SelectedPlayerSlot { get; init; }

    /// <summary>
    ///     Parameter bound to the per-fire <see cref="IEntityValueAt" /> accessor for EDGE breakpoint
    ///     entity reads (<c>&lt;SlotField&gt;.entity.&lt;provider&gt;</c> and <c>player.entity.&lt;provider&gt;</c>).
    ///     When set, those forms resolve through the accessor (positioned per fire-frame by the host)
    ///     instead of the per-player-chain scanner path (<see cref="EntityScanner" />). <c>null</c> otherwise.
    /// </summary>
    public ParameterExpression? EntityValueAtParam { get; init; }

    /// <summary>
    ///     A mutable sink the parser records EDGE-condition usage into (whether it reads entities, refs the
    ///     selected player, and which providers / slot-fields). Drives the host's choice of sync vs async
    ///     hit path and its recompute triggers. <c>null</c> outside the edge breakpoint path.
    /// </summary>
    public EdgeConditionUsage? Usage { get; init; }
}

/// <summary>
///     What an EDGE breakpoint condition references, collected by the parser during compilation.
///     <see cref="NeedsEntityCache" /> selects the host's async entity-replay path;
///     <see cref="ReferencesSelectedPlayer" /> means the condition must recompute on a player-selection
///     change (and short-circuit to no hits when no player is selected).
/// </summary>
public sealed class EdgeConditionUsage
{
    /// <summary>The condition reads a per-player entity provider (host needs the entity-value cache).</summary>
    public bool NeedsEntityCache { get; set; }

    /// <summary>The condition references the selected player (bare <c>player</c> or <c>player.entity.*</c>).</summary>
    public bool ReferencesSelectedPlayer { get; set; }

    /// <summary>Provider names the condition reads (e.g. <c>entity.pawn.health</c>).</summary>
    public HashSet<string> Providers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Event slot-fields the condition reads entities through (e.g. <c>VictimSlot</c>).</summary>
    public HashSet<string> SlotFields { get; } = new(StringComparer.Ordinal);
}

/// <summary>
///     A compiled EDGE breakpoint predicate plus the usage flags the host needs to route hit computation.
///     <see cref="Predicate" /> is a <c>Func&lt;TEvent, IEntityValueAt, bool&gt;</c> (the accessor is
///     unused for pure-event / bare-<c>player</c> conditions — pass a no-op).
/// </summary>
public sealed record EdgeConditionCompileResult(
    Delegate Predicate,
    bool NeedsEntityCache,
    bool ReferencesSelectedPlayer,
    IReadOnlyList<string> Providers,
    IReadOnlyList<string> SlotFields);

/// <summary>
///     A compiled NODE mixed-substrate predicate (<c>Func&lt;TEvent, IEntityValueAt, double&gt;</c>;
///     <c>!= 0</c> = match — the accessor is unused for non-entity conditions, pass a no-op) plus the
///     usage flags the host needs to route hit computation. <see cref="ReferencesSelectedPlayer" /> means
///     the condition references bare <c>player</c> / <c>player.entity.*</c>, so it must recompute on a
///     player-selection change and yield no hits when no player is selected. <see cref="NeedsEntityCache" />
///     means it reads a per-player entity provider, so the host must position the accessor at each fire's
///     pre-frame state (the deferred, cache-backed path) rather than computing synchronously.
/// </summary>
public sealed record NodeMixedCompileResult(
    Delegate Predicate,
    bool ReferencesSelectedPlayer,
    bool NeedsEntityCache);

/// <summary>
///     Parses YAML expression strings (rule conditions, value selectors, parent predicates) into
///     compiled <see cref="Expression{TDelegate}" /> delegates. Resolves identifiers against an
///     <see cref="ExpressionBindings" /> instance per call.
/// </summary>
public static class ExpressionCompiler
{
    /// <summary><see cref="string.Contains(string,StringComparison)" /> — ordinal string containment.</summary>
    private static readonly MethodInfo _stringContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), new[]
        {
            typeof(string), typeof(StringComparison)
        })!;

    /// <summary><see cref="string.StartsWith(string,StringComparison)" /> — ordinal prefix test.</summary>
    private static readonly MethodInfo _stringStartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), new[]
        {
            typeof(string), typeof(StringComparison)
        })!;

    /// <summary>The <see cref="StringComparison.Ordinal" /> constant passed to the string predicates.</summary>
    private static readonly ConstantExpression _ordinalComparison = Expression.Constant(StringComparison.Ordinal);

    /// <summary>
    ///     Compiles a predicate over a single value (the <c>v</c> in expressions like <c>v == 5</c>).
    ///     <c>"active"</c> short-circuits to the identity predicate for bool values.
    /// </summary>
    /// <param name="expression">Expression text to parse.</param>
    /// <param name="valueType">CLR type of the input value the predicate accepts.</param>
    public static Delegate CompileConditionalPredicate(
        string expression, Type valueType)
    {
        if (expression == "active")
        {
            if (valueType == typeof(bool))
            {
                ParameterExpression p = Expression.Parameter(typeof(bool), "v");
                return Expression.Lambda(typeof(Func<bool, bool>), p, p).Compile();
            }

            ParameterExpression pObj = Expression.Parameter(valueType, "v");
            ConstantExpression body = Expression.Constant(true);
            Type ft = typeof(Func<,>).MakeGenericType(valueType, typeof(bool));
            return Expression.Lambda(ft, body, pObj).Compile();
        }

        ParameterExpression param = Expression.Parameter(valueType, "value");
        ExpressionBindings bindings = new()
        {
            ValueParam = param
        };

        Expression parsed = Parse(expression, bindings);
        if (parsed.Type != typeof(bool))
        {
            parsed = Expression.Equal(parsed, Expression.Constant(true));
        }

        Type funcType = typeof(Func<,>).MakeGenericType(valueType, typeof(bool));
        return Expression.Lambda(funcType, parsed, param).Compile();
    }

    /// <summary>
    ///     Compiles a predicate over an event payload (the <c>e</c> in <c>e.fieldName == 1</c>).
    ///     Returns a <c>Func&lt;TEvent, bool&gt;</c> with <c>TEvent</c> set to
    ///     <paramref name="eventType" /> at runtime.
    /// </summary>
    /// <param name="expression">Expression text to parse.</param>
    /// <param name="eventType">CLR type of the event payload.</param>
    /// <param name="fields">Pre-compiled accessors for every field on the event.</param>
    /// <param name="playerSlot">Per-player chain slot; binds <c>player.slot</c>.</param>
    /// <param name="enrichmentNodes">Enrichment nodes available to <c>enrich.xxx</c> references.</param>
    /// <param name="playerTeam">Initial team; fallback for <c>player.team</c> when no context index is bound.</param>
    /// <param name="playerContextIndex">Optional context index providing live team_num for halftime swaps.</param>
    /// <param name="entityScanner">Scanner backing <c>player.entity.*</c> reads; required for such references.</param>
    /// <param name="perPlayerProviders">Per-player provider registry resolving <c>player.entity.*</c> provider names.</param>
    /// <param name="parameterType">
    ///     The delegate's parameter type when it differs from <paramref name="eventType" /> — a game
    ///     event binds the <see cref="GameEvent" /> envelope here so per-fire transport stays
    ///     addressable, while its wire fields still resolve against the payload type. Defaults to
    ///     <paramref name="eventType" /> (net messages and entity-change events have no envelope).
    /// </param>
    public static Delegate CompileEventCondition(
        string expression,
        Type eventType,
        IReadOnlyDictionary<string, EventFieldAccessor> fields,
        int? playerSlot = null,
        IReadOnlyDictionary<string, object>? enrichmentNodes = null,
        int? playerTeam = null,
        PlayerContextIndex? playerContextIndex = null,
        EntityChangeScanner? entityScanner = null,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders = null,
        Type? parameterType = null)
    {
        ParameterExpression param = Expression.Parameter(parameterType ?? eventType, "e");
        ExpressionBindings bindings = new()
        {
            EventParam = param,
            EventPayloadType = eventType,
            EventFields = fields,
            PlayerSlot = playerSlot,
            PlayerTeam = playerTeam,
            EnrichmentNodes = enrichmentNodes,
            PlayerContextIndex = playerContextIndex,
            EntityScanner = entityScanner,
            PerPlayerProviders = perPlayerProviders
        };

        Expression body = Parse(expression, bindings);
        if (body.Type != typeof(bool))
        {
            body = Expression.Equal(body, Expression.Constant(true));
        }

        Type funcType = typeof(Func<,>).MakeGenericType(param.Type, typeof(bool));
        return Expression.Lambda(funcType, body, param).Compile();
    }

    /// <summary>
    ///     Compiles an EDGE breakpoint condition that, beyond the usual <c>event.&lt;field&gt;</c> grammar,
    ///     may reference the filter's selected player (<c>player</c>), event-subject entity reads
    ///     (<c>&lt;SlotField&gt;.entity.&lt;provider&gt;</c>), and the selected player's entity
    ///     (<c>player.entity.&lt;provider&gt;</c>). Returns the compiled
    ///     <c>Func&lt;TEvent, IEntityValueAt, bool&gt;</c> together with the usage flags the host uses to
    ///     route hit computation (entity cache needed? recompute on selection change?). A pure-event or
    ///     bare-<c>player</c> condition compiles here too — its accessor argument is simply never read.
    ///     <para>
    ///         <paramref name="selectedPlayerSlot" /> binds bare <c>player</c> / <c>player.entity.*</c>:
    ///         always pass the current slot (even a negative "all players" sentinel) so the condition still
    ///         compiles and validates; the host short-circuits to no hits when it's negative.
    ///     </para>
    /// </summary>
    public static EdgeConditionCompileResult CompileEdgePlayerEntityCondition(
        string expression,
        Type eventType,
        IReadOnlyDictionary<string, EventFieldAccessor> fields,
        int? selectedPlayerSlot,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders,
        Type? parameterType = null)
    {
        ParameterExpression eventParam = Expression.Parameter(parameterType ?? eventType, "e");
        ParameterExpression entityParam = Expression.Parameter(typeof(IEntityValueAt), "ent");
        EdgeConditionUsage usage = new();

        ExpressionBindings bindings = new()
        {
            EventParam = eventParam,
            EventPayloadType = eventType,
            EventFields = fields,
            SelectedPlayerSlot = selectedPlayerSlot,
            EntityValueAtParam = entityParam,
            PerPlayerProviders = perPlayerProviders,
            Usage = usage
        };

        Expression body = Parse(expression, bindings);
        if (body.Type != typeof(bool))
        {
            body = Expression.Equal(body, Expression.Constant(true));
        }

        Type funcType = typeof(Func<,,>).MakeGenericType(eventParam.Type, typeof(IEntityValueAt), typeof(bool));
        Delegate predicate = Expression.Lambda(funcType, body, eventParam, entityParam).Compile();

        return new EdgeConditionCompileResult(
            predicate, usage.NeedsEntityCache, usage.ReferencesSelectedPlayer,
            usage.Providers.ToList(), usage.SlotFields.ToList());
    }

    /// <summary>
    ///     Compiles an expression that selects a new value from an event payload — e.g. the
    ///     <c>value:</c> field on a <c>TriggerAction.Set</c> trigger. Returns a
    ///     <c>Func&lt;TEvent, TValue&gt;</c>.
    /// </summary>
    /// <param name="expression">Expression text to parse.</param>
    /// <param name="eventType">CLR type of the event payload.</param>
    /// <param name="valueType">CLR type the expression must yield.</param>
    /// <param name="fields">Pre-compiled accessors for every field on the event.</param>
    /// <param name="playerSlot">Per-player chain slot; binds <c>player.slot</c>.</param>
    /// <param name="nodeRef">Optional reference to the host rule's node — bound as <c>node.value</c>.</param>
    /// <param name="enrichmentNodes">Enrichment nodes available to <c>enrich.xxx</c> references.</param>
    /// <param name="entityScanner">Scanner backing <c>player.entity.*</c> reads; required for such references.</param>
    /// <param name="perPlayerProviders">Per-player provider registry resolving <c>player.entity.*</c> provider names.</param>
    /// <param name="parameterType">
    ///     The delegate's parameter type when it differs from <paramref name="eventType" /> — a game
    ///     event binds the <see cref="GameEvent" /> envelope here so per-fire transport stays
    ///     addressable, while its wire fields still resolve against the payload type. Defaults to
    ///     <paramref name="eventType" /> (net messages and entity-change events have no envelope).
    /// </param>
    public static Delegate CompileEventValueSelector(
        string expression,
        Type eventType,
        Type valueType,
        IReadOnlyDictionary<string, EventFieldAccessor> fields,
        int? playerSlot = null,
        object? nodeRef = null,
        IReadOnlyDictionary<string, object>? enrichmentNodes = null,
        EntityChangeScanner? entityScanner = null,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders = null,
        Type? parameterType = null)
    {
        ParameterExpression param = Expression.Parameter(parameterType ?? eventType, "e");

        Expression? nodeValue = null;
        if (nodeRef is not null)
        {
            Type nodeType = nodeRef.GetType();
            PropertyInfo? valueProp = nodeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
            if (valueProp is not null)
            {
                nodeValue = Expression.Property(Expression.Constant(nodeRef), valueProp);
            }
        }

        ExpressionBindings bindings = new()
        {
            EventParam = param,
            EventPayloadType = eventType,
            EventFields = fields,
            PlayerSlot = playerSlot,
            NodeValue = nodeValue,
            EnrichmentNodes = enrichmentNodes,
            EntityScanner = entityScanner,
            PerPlayerProviders = perPlayerProviders
        };

        Expression body = Parse(expression, bindings);
        if (body.Type != valueType)
        {
            body = Expression.Convert(body, valueType);
        }

        Type funcType = typeof(Func<,>).MakeGenericType(param.Type, valueType);
        return Expression.Lambda(funcType, body, param).Compile();
    }

    /// <summary>
    ///     Compiles a KeyedCounter rule's <c>key:</c> expression — the per-fire bucket selector —
    ///     into a <c>Func&lt;TEvent, string&gt;</c>. The expression must yield a <b>string</b>
    ///     (v1 grammar: a single event string-field access such as <c>event.Weapon</c>); any other
    ///     result type is a loud compile error, never a silent <c>ToString()</c> coercion —
    ///     a numeric key would explode the bucket space and is almost certainly an authoring mistake.
    /// </summary>
    /// <param name="expression">Key expression text to parse (e.g. <c>event.Weapon</c>).</param>
    /// <param name="eventType">CLR type of the event payload.</param>
    /// <param name="fields">Pre-compiled accessors for every field on the event.</param>
    /// <param name="playerSlot">Per-player chain slot; binds <c>player.slot</c>.</param>
    /// <param name="enrichmentNodes">Enrichment nodes available to <c>enrich.xxx</c> references.</param>
    /// <param name="parameterType">
    ///     The delegate's parameter type when it differs from <paramref name="eventType" /> — a game
    ///     event binds the <see cref="GameEvent" /> envelope here so per-fire transport stays
    ///     addressable, while its wire fields still resolve against the payload type. Defaults to
    ///     <paramref name="eventType" /> (net messages and entity-change events have no envelope).
    /// </param>
    public static Delegate CompileEventKeySelector(
        string expression,
        Type eventType,
        IReadOnlyDictionary<string, EventFieldAccessor> fields,
        int? playerSlot = null,
        IReadOnlyDictionary<string, object>? enrichmentNodes = null,
        Type? parameterType = null)
    {
        ParameterExpression param = Expression.Parameter(parameterType ?? eventType, "e");
        ExpressionBindings bindings = new()
        {
            EventParam = param,
            EventPayloadType = eventType,
            EventFields = fields,
            PlayerSlot = playerSlot,
            EnrichmentNodes = enrichmentNodes
        };

        Expression body = Parse(expression, bindings);
        if (body.Type != typeof(string))
        {
            throw new InvalidOperationException(
                $"Key expression '{expression}' must produce a string, but produces "
                + $"{body.Type.Name}. v1 keys are event string fields, e.g. \"event.Weapon\".");
        }

        Type funcType = typeof(Func<,>).MakeGenericType(param.Type, typeof(string));
        return Expression.Lambda(funcType, body, param).Compile();
    }

    /// <summary>
    ///     Compiles an expression that reads values from named nodes in a lookup dictionary.
    ///     Identifiers in the expression are resolved to node Value properties.
    ///     Returns a <c>Func&lt;double&gt;</c> suitable for <see cref="Nodes.ComputedStatNode" />.
    /// </summary>
    public static Func<double> CompileNodeExpression(
        string expression,
        IReadOnlyDictionary<string, object> nodeLookup)
    {
        ExpressionBindings bindings = new()
        {
            EnrichmentNodes = nodeLookup
        };
        Expression body = Parse(expression, bindings);
        if (body.Type != typeof(double))
        {
            body = Expression.Convert(body, typeof(double));
        }

        return Expression.Lambda<Func<double>>(body).Compile();
    }

    /// <summary>
    ///     Compiles a boolean predicate over snapshot node values — the multi-source counterpart to
    ///     <see cref="CompileNodeExpression" /> for the Rulesets v2 planner's A2 multi-source
    ///     conditional edges (<c>a + b &gt; 5</c> over N declared sibling sources).
    ///     Identifiers resolve against <paramref name="nodeLookup" /> exactly as
    ///     <see cref="CompileNodeExpression" /> does (captured node proxies read at invoke); the whole
    ///     expression must evaluate to <see cref="bool" /> (a comparison or boolean combination), so
    ///     the compiled delegate feeds <see cref="Abstractions.ConditionalEdge.FromAll" /> directly.
    /// </summary>
    /// <param name="expression">The v1-grammar boolean expression over node names.</param>
    /// <param name="nodeLookup">Identifier → node object; values are read at each invocation.</param>
    /// <returns>A parameterless predicate reading the current node values.</returns>
    /// <exception cref="InvalidOperationException">The expression does not evaluate to a boolean.</exception>
    public static Func<bool> CompileNodeBoolExpression(
        string expression,
        IReadOnlyDictionary<string, object> nodeLookup)
    {
        ExpressionBindings bindings = new()
        {
            EnrichmentNodes = nodeLookup
        };
        Expression body = Parse(expression, bindings);
        if (body.Type != typeof(bool))
        {
            throw new InvalidOperationException(
                $"multi-source predicate '{expression}' must evaluate to a boolean, got {body.Type.Name}.");
        }

        return Expression.Lambda<Func<bool>>(body).Compile();
    }

    /// <summary>
    ///     Compiles a node condition that <em>mixes</em> snapshot state with one input event's fields —
    ///     <c>value == true &amp;&amp; input.player_death.IsHeadshot == true</c>. State identifiers
    ///     (<c>value</c>, other node names, <c>entity.*</c>) resolve against <paramref name="nodeLookup" />
    ///     exactly as <see cref="CompileNodeExpression" /> does (captured proxies read at invoke); the
    ///     <c>input.&lt;event&gt;.&lt;field&gt;</c> references resolve off an event payload parameter.
    ///     A bare <c>player</c> reference (e.g. <c>input.player_death.Attacker == player</c>) resolves to
    ///     <paramref name="selectedPlayerSlot" />, the filter's selected slot — pass it (even a negative
    ///     "all players" sentinel) so the condition still compiles; the host short-circuits to no hits when
    ///     it's negative. Event-subject entity reads (<c>input.player_death.UserId.entity.pawn.health</c>)
    ///     and the selected player's entity (<c>player.entity.*</c>) resolve through the
    ///     <see cref="IEntityValueAt" /> accessor parameter when <paramref name="perPlayerProviders" /> is
    ///     supplied; the host positions that accessor at each fire's PRE-FRAME state. Returns the compiled
    ///     <c>Func&lt;TEvent, IEntityValueAt, double&gt;</c> together with usage flags (references the
    ///     selected player? needs the entity cache?) so the caller can drive it over the event's fire
    ///     indices and route the sync vs deferred (cache-backed) hit path.
    /// </summary>
    public static NodeMixedCompileResult CompileNodeMixedExpression(
        string expression,
        IReadOnlyDictionary<string, object> nodeLookup,
        string inputEventName,
        Type eventType,
        IReadOnlyDictionary<string, EventFieldAccessor> fields,
        int? selectedPlayerSlot = null,
        PerPlayerEntityValueProviderRegistry? perPlayerProviders = null,
        Type? parameterType = null)
    {
        ParameterExpression eventParam = Expression.Parameter(parameterType ?? eventType, "ev");
        ParameterExpression entityParam = Expression.Parameter(typeof(IEntityValueAt), "ent");
        EdgeConditionUsage usage = new();
        ExpressionBindings bindings = new()
        {
            EnrichmentNodes = nodeLookup,
            InputEvent = (inputEventName, eventParam, fields),
            InputEventPayloadType = eventType,
            SelectedPlayerSlot = selectedPlayerSlot,
            EntityValueAtParam = entityParam,
            PerPlayerProviders = perPlayerProviders,
            Usage = usage
        };

        Expression body = Parse(expression, bindings);
        if (body.Type != typeof(double))
        {
            body = Expression.Convert(body, typeof(double));
        }

        Type funcType = typeof(Func<,,>).MakeGenericType(eventParam.Type, typeof(IEntityValueAt), typeof(double));
        Delegate predicate = Expression.Lambda(funcType, body, eventParam, entityParam).Compile();
        return new NodeMixedCompileResult(predicate, usage.ReferencesSelectedPlayer, usage.NeedsEntityCache);
    }

    /// <summary>
    ///     The closed set of builtin function names an identifier-<c>(</c> pair is lowered as a call
    ///     for. Mirrors the checker's <c>ExpressionParser.Functions</c> keys so the runtime accepts
    ///     exactly what the checked language admits. Numeric <c>floor</c>/<c>abs</c>/<c>min</c>/<c>max</c>
    ///     and the string predicates <c>contains</c>/<c>startswith</c> are all evaluable
    ///     (<see cref="BuildFunctionCall" />).
    /// </summary>
    private static bool IsBuiltinFunction(string name) =>
        name is "floor" or "abs" or "min" or "max" or "contains" or "startswith";

    /// <summary>
    ///     Parses a function call — the caller has consumed the function name and <paramref name="pos" />
    ///     sits on the opening <c>(</c>. Arguments are full (comma-separated) sub-expressions; the closing
    ///     <c>)</c> is required. Lowering is delegated to <see cref="BuildFunctionCall" />.
    /// </summary>
    private static MethodCallExpression ParseFunctionCall(string name, Token[] tokens, ref int pos, ExpressionBindings b)
    {
        pos++; // consume '('
        List<Expression> args = new();
        if (pos < tokens.Length && tokens[pos].Kind != TokenKind.RParen)
        {
            args.Add(ParseOr(tokens, ref pos, b));
            while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Comma)
            {
                pos++; // consume ','
                args.Add(ParseOr(tokens, ref pos, b));
            }
        }

        if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.RParen)
        {
            throw new InvalidOperationException($"Expected ')' to close {name}(...)");
        }

        pos++; // consume ')'
        return BuildFunctionCall(name, args);
    }

    /// <summary>
    ///     Lowers a parsed builtin call to a runtime expression. Numeric functions convert their
    ///     arguments to <see cref="double" /> and dispatch to the matching <see cref="Math" /> method
    ///     (<c>floor</c>→<see cref="Math.Floor(double)" />, <c>abs</c>→<see cref="Math.Abs(double)" />,
    ///     <c>min</c>/<c>max</c>→<see cref="Math.Min(double,double)" />/<see cref="Math.Max(double,double)" />).
    ///     The string predicates <c>contains</c>/<c>startswith</c> lower to
    ///     <see cref="string.Contains(string,StringComparison)" /> /
    ///     <see cref="string.StartsWith(string,StringComparison)" /> with
    ///     <see cref="StringComparison.Ordinal" /> (matching the checker's documented ordinal,
    ///     culture-invariant semantics); both operands must be string-typed.
    /// </summary>
    private static MethodCallExpression BuildFunctionCall(string name, List<Expression> args)
    {
        switch (name)
        {
            case "floor":
                RequireArity(name, args, 1);
                return Expression.Call(typeof(Math), nameof(Math.Floor), null, ToDouble(args[0]));
            case "abs":
                RequireArity(name, args, 1);
                return Expression.Call(typeof(Math), nameof(Math.Abs), null, ToDouble(args[0]));
            case "min":
                RequireArity(name, args, 2);
                return Expression.Call(typeof(Math), nameof(Math.Min), null, ToDouble(args[0]), ToDouble(args[1]));
            case "max":
                RequireArity(name, args, 2);
                return Expression.Call(typeof(Math), nameof(Math.Max), null, ToDouble(args[0]), ToDouble(args[1]));
            case "contains":
                RequireArity(name, args, 2);
                return Expression.Call(
                    RequireString(name, args[0], 0), _stringContainsMethod,
                    RequireString(name, args[1], 1), _ordinalComparison);
            case "startswith":
                RequireArity(name, args, 2);
                return Expression.Call(
                    RequireString(name, args[0], 0), _stringStartsWithMethod,
                    RequireString(name, args[1], 1), _ordinalComparison);
            default:
                throw new InvalidOperationException($"Unknown function: {name}");
        }
    }

    /// <summary>
    ///     Verifies a string-function operand is string-typed (it always is by the checker's
    ///     construction; this is the runtime guard against a mis-wired non-string argument).
    /// </summary>
    private static Expression RequireString(string name, Expression e, int argIndex)
    {
        if (e.Type != typeof(string))
        {
            throw new InvalidOperationException(
                $"Function '{name}' argument {argIndex + 1} must be a string, but is {e.Type.Name}.");
        }

        return e;
    }

    private static void RequireArity(string name, List<Expression> args, int expected)
    {
        if (args.Count != expected)
        {
            throw new InvalidOperationException(
                $"Function '{name}' expects {expected} argument(s), got {args.Count}.");
        }
    }

    private static Expression ToDouble(Expression e) =>
        e.Type == typeof(double) ? e : Expression.Convert(e, typeof(double));

    /// <summary>
    ///     The expression <c>event.&lt;field&gt;</c> reads its property off. For a game event the bound
    ///     parameter is the <see cref="GameEvent" /> envelope, so a wire field has to reach through
    ///     <see cref="GameEvent.Payload" /> and cast to the payload record — except for a synthesized
    ///     event, which IS a <see cref="GameEvent" /> subclass declaring its fields directly and so
    ///     needs only the cast. Net-message payloads and entity-change events have no envelope; their
    ///     parameter is already the subject.
    /// </summary>
    private static Expression EventFieldSubject(ParameterExpression param, Type payloadType)
    {
        if (param.Type == payloadType)
        {
            return param;
        }

        return typeof(GameEvent).IsAssignableFrom(payloadType)
            ? Expression.Convert(param, payloadType)
            : Expression.Convert(
                Expression.Property(param, nameof(GameEvent.Payload)), payloadType);
    }

    /// <summary>
    ///     Resolves an <c>event.&lt;field&gt;</c> reference. Wire fields resolve against the payload
    ///     type; when the parameter is the envelope, a name that is not a wire field falls back to the
    ///     envelope's own per-fire transport (<c>ServerTick</c>, <c>GameTick</c>, <c>FrameNumber</c>) —
    ///     which is how the v2 <c>event.tick</c> instant resolves, since it has no wire field at all.
    /// </summary>
    private static Expression ResolveEventField(ExpressionBindings b, string fieldName)
    {
        ParameterExpression param = b.EventParam!;
        Type payloadType = b.EventPayloadType ?? param.Type;

        PropertyInfo? prop = payloadType.GetProperty(fieldName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (prop is not null)
        {
            return WidenNarrowIntegral(
                Expression.Property(EventFieldSubject(param, payloadType), prop));
        }

        return ResolveEnvelopeTransport(param, payloadType, fieldName)
               ?? throw new InvalidOperationException($"Unknown event field: {fieldName}");
    }

    /// <summary>
    ///     Resolves per-fire transport (<c>ServerTick</c>, <c>GameTick</c>, <c>FrameNumber</c>) off an
    ///     envelope-typed parameter, with <c>tick</c> aliased to <c>ServerTick</c> — the same alias
    ///     the ruleset loader rewrites before this compiler ever sees a ruleset expression, applied
    ///     here so a condition that arrives raw (a breakpoint) resolves identically. Payload fields
    ///     take precedence at both call sites, so a wire field named <c>tick</c> would still win.
    ///     <c>null</c> when the parameter IS the subject (net message, entity change — no envelope)
    ///     or the name matches no transport property.
    /// </summary>
    private static Expression? ResolveEnvelopeTransport(
        ParameterExpression param, Type payloadType, string fieldName)
    {
        if (param.Type == payloadType)
        {
            return null;
        }

        string transportName = fieldName.Equals("tick", StringComparison.OrdinalIgnoreCase)
            ? nameof(GameEvent.ServerTick)
            : fieldName;
        PropertyInfo? transport = param.Type.GetProperty(transportName,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return transport is null ? null : WidenNarrowIntegral(Expression.Property(param, transport));
    }

    private static (Expression Left, Expression Right) Coerce(Expression left, Expression right)
    {
        if (left.Type == right.Type)
        {
            return (left, right);
        }

        if (left.Type == typeof(int) && right.Type == typeof(double))
        {
            return (Expression.Convert(left, typeof(double)), right);
        }

        if (left.Type == typeof(double) && right.Type == typeof(int))
        {
            return (left, Expression.Convert(right, typeof(double)));
        }

        if (left.Type == typeof(int) && right.Type == typeof(float))
        {
            return (Expression.Convert(left, typeof(float)), right);
        }

        if (left.Type == typeof(float) && right.Type == typeof(int))
        {
            return (left, Expression.Convert(right, typeof(float)));
        }

        // A number-map lookup yields double? (null on miss); lift the numeric other side to double?
        // so a comparison such as `weapon_tier[event.Weapon] > 2` is a lifted (null-safe) comparison.
        if (left.Type == typeof(double?) && right.Type is var rt && (rt == typeof(int) || rt == typeof(double) || rt == typeof(float)))
        {
            return (left, Expression.Convert(right, typeof(double?)));
        }

        if (right.Type == typeof(double?) && left.Type is var lt && (lt == typeof(int) || lt == typeof(double) || lt == typeof(float)))
        {
            return (Expression.Convert(left, typeof(double?)), right);
        }

        return (left, right);
    }

    private static Expression MakeSafeDivide(Expression left, Expression right)
    {
        if (left.Type == typeof(double) || right.Type == typeof(double))
        {
            if (left.Type != typeof(double))
            {
                left = Expression.Convert(left, typeof(double));
            }

            if (right.Type != typeof(double))
            {
                right = Expression.Convert(right, typeof(double));
            }

            return Expression.Call(typeof(ExpressionCompiler), nameof(SafeDivide), null, left, right);
        }

        if (left.Type == typeof(int) && right.Type == typeof(int))
        {
            return Expression.Call(typeof(ExpressionCompiler), nameof(SafeDivideInt), null, left, right);
        }

        return Expression.Divide(left, right);
    }

    /// <summary>
    ///     Resolves a <c>player.entity.&lt;provider-name&gt;</c> reference to a fixed-slot read of the
    ///     scanner's pre-frame snapshot. The value is PRE-FRAME relative to the in-flight frame (the
    ///     same previous-frame semantics <c>HurtTeamEnrichmentEdge</c> relies on). A null snapshot value
    ///     degrades to the provider's default; a missing scanner/registry/slot is a compile-time error.
    /// </summary>
    private static UnaryExpression ResolvePlayerEntity(ExpressionBindings b, string providerName)
    {
        if (b.EntityScanner is null || b.PerPlayerProviders is null || !b.PlayerSlot.HasValue)
        {
            throw new InvalidOperationException(
                $"'player.{providerName}' requires per-player entity providers and a player slot, but none are " +
                "bound. Build the rule chain with a PerPlayerEntityValueProviderRegistry (so the entity scanner " +
                "is created) for per-player chains.");
        }

        IPerPlayerEntityValueProvider? provider = b.PerPlayerProviders.Get(providerName);
        if (provider is null)
        {
            throw new InvalidOperationException(
                $"Unknown per-player entity provider: '{providerName}' (from 'player.{providerName}').");
        }

        MethodInfo getPreFrame =
            typeof(EntityChangeScanner).GetMethod(nameof(EntityChangeScanner.GetPreFrameValue))!;
        Expression call = Expression.Call(
            Expression.Constant(b.EntityScanner),
            getPreFrame,
            Expression.Constant(provider, typeof(IPerPlayerEntityValueProvider)),
            Expression.Constant(b.PlayerSlot.Value)); // object? (boxed value or null)

        // null snapshot → provider default (0 for value types, "" for string), then unbox/cast to ValueType.
        object? boxedDefault = provider.ValueType.IsValueType
            ? Activator.CreateInstance(provider.ValueType)
            : provider.ValueType == typeof(string)
                ? ""
                : null;
        Expression coalesced = boxedDefault is null
            ? call
            : Expression.Coalesce(call, Expression.Constant(boxedDefault, typeof(object)));
        return Expression.Convert(coalesced, provider.ValueType);
    }

    /// <summary>
    ///     Resolves a B5 role-handle event-subject entity read (<c>&lt;SlotField&gt;.entity.&lt;provider&gt;</c>)
    ///     on a game-event trigger condition to a <see cref="EntityChangeScanner.GetPreFrameValue" /> call
    ///     keyed by <paramref name="slotExpr" /> — the ROLE's slot read PER FIRE from the event field
    ///     (<c>VictimSlot</c>), not the chain's fixed subject slot. Same PRE-FRAME semantics, null-default
    ///     coalescing, and value-type convert as <see cref="ResolvePlayerEntity" />; the scanner snapshots
    ///     every live slot, so any role's slot resolves. A missing scanner/registry is the same compile-time
    ///     error <see cref="ResolvePlayerEntity" /> raises (so a no-demo build surfaces the same marker).
    /// </summary>
    private static UnaryExpression ResolveEventSlotEntity(ExpressionBindings b, string providerName, Expression slotExpr)
    {
        if (b.EntityScanner is null || b.PerPlayerProviders is null)
        {
            throw new InvalidOperationException(
                $"a role-handle entity read ('{providerName}') requires per-player entity providers and a player " +
                "slot, but none are bound. Build the rule chain with a PerPlayerEntityValueProviderRegistry (so the " +
                "entity scanner is created) for per-player chains.");
        }

        IPerPlayerEntityValueProvider? provider = b.PerPlayerProviders.Get(providerName);
        if (provider is null)
        {
            throw new InvalidOperationException(
                $"Unknown per-player entity provider: '{providerName}' (from a role-handle entity read).");
        }

        MethodInfo getPreFrame =
            typeof(EntityChangeScanner).GetMethod(nameof(EntityChangeScanner.GetPreFrameValue))!;
        Expression slotInt = slotExpr.Type == typeof(int) ? slotExpr : Expression.Convert(slotExpr, typeof(int));
        Expression call = Expression.Call(
            Expression.Constant(b.EntityScanner),
            getPreFrame,
            Expression.Constant(provider, typeof(IPerPlayerEntityValueProvider)),
            slotInt); // object? (boxed value or null)

        // null snapshot → provider default (0 for value types, "" for string), then unbox/cast to ValueType.
        object? boxedDefault = provider.ValueType.IsValueType
            ? Activator.CreateInstance(provider.ValueType)
            : provider.ValueType == typeof(string)
                ? ""
                : null;
        Expression coalesced = boxedDefault is null
            ? call
            : Expression.Coalesce(call, Expression.Constant(boxedDefault, typeof(object)));
        return Expression.Convert(coalesced, provider.ValueType);
    }

    /// <summary>
    ///     Resolves an EDGE breakpoint entity read (<c>&lt;SlotField&gt;.entity.&lt;provider&gt;</c> or
    ///     <c>player.entity.&lt;provider&gt;</c>) to a call into the bound <see cref="IEntityValueAt" />
    ///     accessor at <paramref name="slotExpr" />, coalesced to the provider's default and converted to
    ///     its value type (the same null handling as <see cref="ResolvePlayerEntity" />). The host
    ///     positions the accessor at the fire's PRE-FRAME state before each invoke. Records the read in
    ///     <see cref="ExpressionBindings.Usage" /> so the host knows it needs the entity cache.
    /// </summary>
    private static UnaryExpression ResolveEdgeEntity(ExpressionBindings b, string providerName, Expression slotExpr)
    {
        IPerPlayerEntityValueProvider? provider = b.PerPlayerProviders?.Get(providerName);
        if (provider is null)
        {
            throw new InvalidOperationException($"Unknown per-player entity provider: '{providerName}'.");
        }

        if (b.Usage is not null)
        {
            b.Usage.NeedsEntityCache = true;
            b.Usage.Providers.Add(providerName);
        }

        MethodInfo getValue = typeof(IEntityValueAt).GetMethod(nameof(IEntityValueAt.GetValue))!;
        Expression slotInt = slotExpr.Type == typeof(int) ? slotExpr : Expression.Convert(slotExpr, typeof(int));
        Expression call = Expression.Call(
            b.EntityValueAtParam!, getValue, Expression.Constant(providerName), slotInt); // object?

        object? boxedDefault = provider.ValueType.IsValueType
            ? Activator.CreateInstance(provider.ValueType)
            : provider.ValueType == typeof(string)
                ? ""
                : null;
        Expression coalesced = boxedDefault is null
            ? call
            : Expression.Coalesce(call, Expression.Constant(boxedDefault, typeof(object)));
        return Expression.Convert(coalesced, provider.ValueType);
    }

    // ── Parser ──────────────────────────────────────────────────────────────

    private static Expression Parse(string expression, ExpressionBindings bindings)
    {
        Token[] tokens = Tokenize(expression);
        int pos = 0;
        Expression result = ParseOr(tokens, ref pos, bindings);
        return result;
    }

    private static Expression ParseAdditive(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        Expression left = ParseMultiplicative(tokens, ref pos, b);
        while (pos < tokens.Length && tokens[pos].Kind is TokenKind.OpPlus or TokenKind.OpMinus)
        {
            TokenKind op = tokens[pos].Kind;
            pos++;
            Expression right = ParseMultiplicative(tokens, ref pos, b);
            (left, right) = Coerce(left, right);
            left = op == TokenKind.OpPlus ? Expression.Add(left, right) : Expression.Subtract(left, right);
        }

        return left;
    }

    private static Expression ParseAnd(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        Expression left = ParseComparison(tokens, ref pos, b);
        while (pos < tokens.Length && tokens[pos].Kind == TokenKind.OpAnd)
        {
            pos++;
            Expression right = ParseComparison(tokens, ref pos, b);
            left = Expression.AndAlso(left, right);
        }

        return left;
    }

    private static Expression ParseComparison(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        Expression left = ParseAdditive(tokens, ref pos, b);

        if (pos < tokens.Length)
        {
            TokenKind kind = tokens[pos].Kind;
            if (kind is TokenKind.OpEq or TokenKind.OpNeq or TokenKind.OpGt
                or TokenKind.OpGte or TokenKind.OpLt or TokenKind.OpLte)
            {
                pos++;
                Expression right = ParseAdditive(tokens, ref pos, b);
                (left, right) = Coerce(left, right);
                left = kind switch
                {
                    TokenKind.OpEq => Expression.Equal(left, right),
                    TokenKind.OpNeq => Expression.NotEqual(left, right),
                    TokenKind.OpGt => Expression.GreaterThan(left, right),
                    TokenKind.OpGte => Expression.GreaterThanOrEqual(left, right),
                    TokenKind.OpLt => Expression.LessThan(left, right),
                    TokenKind.OpLte => Expression.LessThanOrEqual(left, right),
                    _ => left
                };
            }
        }

        return left;
    }

    private static Expression ParseMultiplicative(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        Expression left = ParseUnary(tokens, ref pos, b);
        while (pos < tokens.Length && tokens[pos].Kind is TokenKind.OpMul or TokenKind.OpDiv or TokenKind.OpMod)
        {
            TokenKind op = tokens[pos].Kind;
            pos++;
            Expression right = ParseUnary(tokens, ref pos, b);
            (left, right) = Coerce(left, right);
            left = op switch
            {
                TokenKind.OpMul => Expression.Multiply(left, right),
                TokenKind.OpDiv => MakeSafeDivide(left, right),
                TokenKind.OpMod => Expression.Modulo(left, right),
                _ => left
            };
        }

        return left;
    }

    private static Expression ParseOr(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        Expression left = ParseAnd(tokens, ref pos, b);
        while (pos < tokens.Length && tokens[pos].Kind == TokenKind.OpOr)
        {
            pos++;
            Expression right = ParseAnd(tokens, ref pos, b);
            left = Expression.OrElse(left, right);
        }

        return left;
    }

    private static Expression ParsePrimary(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        if (pos >= tokens.Length)
        {
            throw new InvalidOperationException("Unexpected end of expression.");
        }

        Token tok = tokens[pos];

        if (tok.Kind == TokenKind.LBrace)
        {
            return ParseMapLookup(tokens, ref pos, b);
        }

        if (tok.Kind == TokenKind.LParen)
        {
            pos++;
            Expression inner = ParseOr(tokens, ref pos, b);
            if (pos < tokens.Length && tokens[pos].Kind == TokenKind.RParen)
            {
                pos++;
            }

            return inner;
        }

        if (tok.Kind == TokenKind.Number)
        {
            pos++;
            if (tok.Text.Contains('.'))
            {
                return Expression.Constant(double.Parse(tok.Text, CultureInfo.InvariantCulture));
            }

            return Expression.Constant(int.Parse(tok.Text, CultureInfo.InvariantCulture));
        }

        if (tok.Kind == TokenKind.String)
        {
            pos++;
            return Expression.Constant(tok.Text[1..^1]); // strip quotes
        }

        if (tok.Kind == TokenKind.Identifier)
        {
            string name = tok.Text;
            pos++;

            // Runtime function-call lowering — mirrors the checker's closed function set
            // (Analysis.Rules ExpressionParser.Functions). An identifier immediately followed by '('
            // whose name is a known builtin is a call. Numeric floor/abs/min/max and the string
            // predicates contains/startswith all evaluate here (see BuildFunctionCall).
            if (pos < tokens.Length && tokens[pos].Kind == TokenKind.LParen && IsBuiltinFunction(name))
            {
                return ParseFunctionCall(name, tokens, ref pos, b);
            }

            if (name == "true")
            {
                return Expression.Constant(true);
            }

            if (name == "false")
            {
                return Expression.Constant(false);
            }

            if (name == "value" && b.ValueParam is not null)
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    return ResolveDotChain(b.ValueParam, tokens, ref pos);
                }

                return b.ValueParam;
            }

            if (name == "event" && b.EventParam is not null)
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    pos++; // skip dot
                    if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                    {
                        throw new InvalidOperationException("Expected field name after 'event.'");
                    }

                    string fieldName = tokens[pos].Text;
                    pos++;

                    return ResolveEventField(b, fieldName);
                }

                return b.EventParam;
            }

            // `input.<event>.<field>` — a node condition referencing the payload of the event that
            // activates it (e.g. on a kill-streak node, `input.player_death.IsHeadshot`). The <event>
            // segment must match the single bound input event (one event fires per message, so a
            // node-input condition is scoped to one event type); <field> resolves off its payload param,
            // exactly like `event.<field>`. A trailing `.entity.<provider>` on a `*Slot` field
            // (`input.player_death.UserId.entity.pawn.health`) is the node event-subject entity read.
            if (name == "input" && b.InputEvent is { } input)
            {
                if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Dot)
                {
                    throw new InvalidOperationException("Expected '.<event>.<field>' after 'input'");
                }

                pos++; // skip dot
                if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                {
                    throw new InvalidOperationException("Expected an event name after 'input.'");
                }

                string evName = tokens[pos].Text;
                pos++;
                if (!string.Equals(evName, input.EventName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Unknown input event '{evName}' — this node's input is '{input.EventName}'");
                }

                if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Dot)
                {
                    throw new InvalidOperationException($"Expected '.<field>' after 'input.{evName}'");
                }

                pos++; // skip dot
                if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                {
                    throw new InvalidOperationException($"Expected a field name after 'input.{evName}.'");
                }

                string fieldName = tokens[pos].Text;
                pos++;
                Type inputPayloadType = b.InputEventPayloadType ?? input.Param.Type;
                if (!input.Fields.ContainsKey(fieldName))
                {
                    // Not a wire field — per-fire transport (`input.<event>.tick`) resolves off the
                    // envelope parameter when there is one, mirroring `event.<field>` above.
                    return ResolveEnvelopeTransport(input.Param, inputPayloadType, fieldName)
                           ?? throw new InvalidOperationException(
                               $"Unknown field '{fieldName}' on input event '{evName}'");
                }

                PropertyInfo? fieldProp = inputPayloadType.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                if (fieldProp is null)
                {
                    throw new InvalidOperationException($"Unknown field '{fieldName}' on input event '{evName}'");
                }

                // `input.<event>.<SlotField>.entity.<provider>` — event-subject entity read on a node. The
                // slot is read PER-FIRE from the field; the value comes from the bound accessor (positioned
                // at the fire's PRE-FRAME state by the host). Only when an accessor is bound and a `*Slot`
                // field is followed by `.entity.…`; otherwise the field resolves to its raw value as before.
                if (b.EntityValueAtParam is not null
                    && pos + 1 < tokens.Length
                    && tokens[pos].Kind == TokenKind.Dot
                    && tokens[pos + 1].Kind == TokenKind.Identifier && tokens[pos + 1].Text == "entity")
                {
                    if (!IsPlayerSlotField(fieldName, fieldProp.PropertyType))
                    {
                        throw new InvalidOperationException(
                            $"Entity reads need a player-slot field; '{fieldName}' is not one.");
                    }

                    pos++; // skip the dot before 'entity'
                    List<string> entityPath = new();
                    while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Identifier)
                    {
                        entityPath.Add(tokens[pos].Text);
                        pos++;
                        if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                        {
                            pos++; // consume the dot between path segments
                        }
                        else
                        {
                            break;
                        }
                    }

                    Expression slotExpr = Expression.Property(
                        EventFieldSubject(input.Param, inputPayloadType), fieldProp);
                    if (b.Usage is not null)
                    {
                        b.Usage.SlotFields.Add(fieldName);
                    }

                    return ResolveEdgeEntity(b, string.Join(".", entityPath), slotExpr);
                }

                return WidenNarrowIntegral(Expression.Property(
                    EventFieldSubject(input.Param, inputPayloadType), fieldProp));
            }

            if (name == "node")
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    pos++; // skip dot
                    if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                    {
                        throw new InvalidOperationException("Expected 'value' after 'node.'");
                    }

                    string member = tokens[pos].Text;
                    pos++;

                    if (member == "value" && b.NodeValue is not null)
                    {
                        return b.NodeValue;
                    }

                    throw new InvalidOperationException($"Unknown node member: {member}");
                }
            }

            if (name == "player")
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    pos++; // skip dot
                    if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                    {
                        throw new InvalidOperationException("Expected member after 'player.'");
                    }

                    string member = tokens[pos].Text;
                    pos++;

                    // `player.entity.<provider-name>` — a slot-scoped read of a per-player entity
                    // value (e.g. `player.entity.pawn.health`). The path after `player.` is the
                    // provider's registered Name; the slot is the per-player chain's compile-time
                    // constant, so this compiles to a fixed-slot scanner call (no runtime dispatch).
                    if (member == "entity")
                    {
                        List<string> entityPath = new()
                        {
                            "entity"
                        };
                        while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                        {
                            pos++; // skip dot
                            if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                            {
                                break;
                            }

                            entityPath.Add(tokens[pos].Text);
                            pos++;
                        }

                        string providerName = string.Join(".", entityPath);

                        // EDGE breakpoint substrate: read via the positioned accessor at the SELECTED
                        // slot (the host's EntityValueCache). Falls back to the per-player-chain scanner
                        // path (fixed compile-time slot) when no accessor is bound.
                        if (b.EntityValueAtParam is not null && b.SelectedPlayerSlot.HasValue)
                        {
                            if (b.Usage is not null)
                            {
                                b.Usage.ReferencesSelectedPlayer = true;
                            }

                            return ResolveEdgeEntity(b, providerName, Expression.Constant(b.SelectedPlayerSlot.Value));
                        }

                        return ResolvePlayerEntity(b, providerName);
                    }

                    if (member == "slot" && b.PlayerSlot.HasValue)
                    {
                        return Expression.Constant(b.PlayerSlot.Value);
                    }

                    if (member == "team")
                    {
                        // Runtime lookup: player.team must reflect halftime side swaps,
                        // so resolve via PlayerContextIndex.GetCurrentTeam(slot) at evaluation
                        // time. Fall back to compile-time constant only if no index is provided.
                        if (b.PlayerContextIndex is not null && b.PlayerSlot.HasValue)
                        {
                            ConstantExpression idxConst = Expression.Constant(b.PlayerContextIndex);
                            ConstantExpression slotConst = Expression.Constant(b.PlayerSlot.Value);
                            MethodInfo method = typeof(PlayerContextIndex)
                                .GetMethod(nameof(PlayerContextIndex.GetCurrentTeam))!;
                            return Expression.Call(idxConst, method, slotConst);
                        }

                        if (b.PlayerTeam.HasValue)
                        {
                            return Expression.Constant(b.PlayerTeam.Value);
                        }
                    }

                    if (member == "name" && b.PlayerName is not null)
                    {
                        return Expression.Constant(b.PlayerName);
                    }

                    throw new InvalidOperationException($"Unknown player member: {member}");
                }

                // Bare `player` (no dot) — the filter's SELECTED player slot, bound for an EDGE
                // breakpoint comparison (`event.Attacker == player`). Resolves to a compile-time int
                // constant; the host recompiles on a selection change and short-circuits to no hits when
                // no player is selected (slot < 0). The dotted block above returns/throws for every
                // `player.<member>`, so reaching here means a bare reference.
                if (b.SelectedPlayerSlot.HasValue)
                {
                    if (b.Usage is not null)
                    {
                        b.Usage.ReferencesSelectedPlayer = true;
                    }

                    return Expression.Constant(b.SelectedPlayerSlot.Value);
                }
            }

            // `<SlotField>.entity.<provider>` — event-subject entity read (EDGE breakpoint substrate). The
            // leading identifier is an event field that names a player by slot (a `*Slot` field), and the
            // slot is read PER-FIRE from the payload. Only fires when an entity accessor is bound (the edge
            // path) and the field is followed by `.entity.…`. (Node conditions reach the same read via the
            // `input.<event>.<SlotField>.entity.…` branch above, since they name the slot through `input.`.)
            if (b.EntityValueAtParam is not null && b.EventParam is not null && b.EventFields is not null
                && b.EventFields.ContainsKey(name)
                && pos + 1 < tokens.Length
                && tokens[pos].Kind == TokenKind.Dot
                && tokens[pos + 1].Kind == TokenKind.Identifier && tokens[pos + 1].Text == "entity")
            {
                if (!IsPlayerSlotField(name, b.EventFields[name].FieldType))
                {
                    throw new InvalidOperationException(
                        $"Entity reads need a player-slot field; '{name}' is not one.");
                }

                pos++; // skip the dot before 'entity'
                List<string> entityPath = new();
                while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Identifier)
                {
                    entityPath.Add(tokens[pos].Text);
                    pos++;
                    if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                    {
                        pos++; // consume the dot between path segments
                    }
                    else
                    {
                        break;
                    }
                }

                Expression slotExpr = ResolveEventField(b, name);
                if (b.Usage is not null)
                {
                    b.Usage.SlotFields.Add(name);
                }

                return ResolveEdgeEntity(b, string.Join(".", entityPath), slotExpr);
            }

            // `<SlotField>.entity.<provider>` — B5 role-handle event-subject entity read on a GAME-EVENT
            // trigger condition (the v2 planner path; `victim.health` -> `UserId.entity.pawn.health`).
            // Unlike the EDGE substrate above there is no bound IEntityValueAt accessor here — the value
            // comes from the same EntityChangeScanner a `player.entity.*` read uses (ResolvePlayerEntity),
            // but keyed by the slot read PER FIRE from the event field instead of the chain's fixed subject
            // slot. Only fires in the non-edge path (EntityValueAtParam null) so it never hijacks the edge
            // branch above; a game-event condition binds EventParam+EventFields but no accessor.
            if (b.EntityValueAtParam is null && b.EventParam is not null && b.EventFields is not null
                && b.EventFields.ContainsKey(name)
                && pos + 1 < tokens.Length
                && tokens[pos].Kind == TokenKind.Dot
                && tokens[pos + 1].Kind == TokenKind.Identifier && tokens[pos + 1].Text == "entity")
            {
                if (!IsPlayerSlotField(name, b.EventFields[name].FieldType))
                {
                    throw new InvalidOperationException(
                        $"Entity reads need a player-slot field; '{name}' is not one.");
                }

                pos++; // skip the dot before 'entity'
                List<string> entityPath = new();
                while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Identifier)
                {
                    entityPath.Add(tokens[pos].Text);
                    pos++;
                    if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                    {
                        pos++; // consume the dot between path segments
                    }
                    else
                    {
                        break;
                    }
                }

                Expression roleSlot = ResolveEventField(b, name);
                return ResolveEventSlotEntity(b, string.Join(".", entityPath), roleSlot);
            }

            if (name == "context")
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    pos++; // skip dot
                    if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                    {
                        throw new InvalidOperationException("Expected member after 'context.'");
                    }

                    string member = tokens[pos].Text;
                    pos++;

                    if (member == "GameTick" && b.EventParam is not null)
                    {
                        // Per-fire transport, so this reads off the envelope when one is bound. The
                        // same resolution `event.<field>` uses, which is what makes it survive the
                        // move of the tick off the payload record and onto the fire.
                        return ResolveEventField(b, "GameTick");
                    }

                    throw new InvalidOperationException($"Unknown context member: {member}");
                }
            }

            if (name == "enrich" && b.EnrichmentNodes is not null)
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    // Parse full dotted path: enrich.hurt.capped_damage
                    List<string> pathParts = new();
                    while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                    {
                        pos++; // skip dot
                        if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                        {
                            break;
                        }

                        pathParts.Add(tokens[pos].Text);
                        pos++;
                    }

                    string enrichKey = $"enrich.{string.Join(".", pathParts)}";
                    if (b.EnrichmentNodes.TryGetValue(enrichKey, out object? nodeObj))
                    {
                        Type nodeType = nodeObj.GetType();
                        PropertyInfo? valueProp = nodeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        if (valueProp is not null)
                        {
                            return Expression.Property(Expression.Constant(nodeObj), valueProp);
                        }
                    }

                    throw new InvalidOperationException($"Unknown enrichment node: {enrichKey}");
                }
            }

            if (name == "active")
            {
                return Expression.Constant(true);
            }

            // Entity-context dotted references: `entity.game.freeze_period`, `entity.player.health`,
            // … reassemble the dotted key and look it up in _enrichmentNodes — populated by
            // RuleChainBuilder when at least one entity-value provider with that ContextName
            // is registered. Parallel to the `enrich.` branch above.
            if (name == "entity" && b.EnrichmentNodes is not null)
            {
                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                {
                    List<string> pathParts = new();
                    while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
                    {
                        pos++; // skip dot
                        if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
                        {
                            break;
                        }

                        pathParts.Add(tokens[pos].Text);
                        pos++;
                    }

                    string entityKey = "entity." + string.Join(".", pathParts);
                    if (b.EnrichmentNodes.TryGetValue(entityKey, out object? entityNode))
                    {
                        Type nodeType = entityNode.GetType();
                        PropertyInfo? valueProp = nodeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                        if (valueProp is not null)
                        {
                            return Expression.Property(Expression.Constant(entityNode), valueProp);
                        }
                    }

                    throw new InvalidOperationException($"Unknown entity context: {entityKey}");
                }
            }

            // Fallback: try resolving as a node reference from the enrichment/node lookup
            if (b.EnrichmentNodes is not null && b.EnrichmentNodes.TryGetValue(name, out object? fallbackNode))
            {
                Type nodeType = fallbackNode.GetType();
                PropertyInfo? valueProp = nodeType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                if (valueProp is not null)
                {
                    Expression valueExpr = Expression.Property(Expression.Constant(fallbackNode), valueProp);
                    if (valueProp.PropertyType != typeof(double) && valueProp.PropertyType != typeof(int))
                    {
                        return valueExpr;
                    }

                    return valueProp.PropertyType == typeof(int)
                        ? Expression.Convert(valueExpr, typeof(double))
                        : valueExpr;
                }
            }

            throw new InvalidOperationException($"Unknown identifier: {name}");
        }

        throw new InvalidOperationException($"Unexpected token: {tok.Text}");
    }

    /// <summary>
    ///     Parses a map-lookup expression <c>{"k1": v1, "k2": v2}[keyExpr]</c> — the runtime lowering of
    ///     an inlined map-valued <c>define:</c> subscript (spec §3.4). The literal builds a
    ///     constant dictionary (string- or number-valued, per the entries' uniform type) and the trailing
    ///     <c>[key]</c> becomes a lookup returning the mapped value or <c>null</c> on a miss — string maps
    ///     yield <see cref="string" /> (nullable), number maps yield <see cref="Nullable{Double}" />. The
    ///     enclosing subscript is mandatory: a bare map value is a checker error, so <c>V1ExpressionWriter</c>
    ///     never emits a map literal without one.
    /// </summary>
    private static MethodCallExpression ParseMapLookup(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        pos++; // consume '{'
        Dictionary<string, string> stringEntries = new(StringComparer.Ordinal);
        Dictionary<string, double> numberEntries = new(StringComparer.Ordinal);
        bool sawString = false;
        bool sawNumber = false;

        if (pos < tokens.Length && tokens[pos].Kind != TokenKind.RBrace)
        {
            while (true)
            {
                if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.String)
                {
                    throw new InvalidOperationException("Expected a quoted string key in a map literal.");
                }

                string key = tokens[pos].Text[1..^1]; // strip quotes
                pos++;

                if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Colon)
                {
                    throw new InvalidOperationException($"Expected ':' after map key \"{key}\".");
                }

                pos++; // consume ':'

                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.String)
                {
                    sawString = true;
                    stringEntries[key] = tokens[pos].Text[1..^1];
                    pos++;
                }
                else if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Number)
                {
                    sawNumber = true;
                    numberEntries[key] = double.Parse(tokens[pos].Text, CultureInfo.InvariantCulture);
                    pos++;
                }
                else
                {
                    throw new InvalidOperationException($"Expected a string or number value for map key \"{key}\".");
                }

                if (pos < tokens.Length && tokens[pos].Kind == TokenKind.Comma)
                {
                    pos++; // consume ','
                    continue;
                }

                break;
            }
        }

        if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.RBrace)
        {
            throw new InvalidOperationException("Expected '}' to close the map literal.");
        }

        pos++; // consume '}'

        if (sawString && sawNumber)
        {
            throw new InvalidOperationException("A map literal mixes string and number values.");
        }

        if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.LBracket)
        {
            throw new InvalidOperationException("A map literal must be indexed with a [key] lookup.");
        }

        pos++; // consume '['
        Expression keyExpr = ParseOr(tokens, ref pos, b);
        if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.RBracket)
        {
            throw new InvalidOperationException("Expected ']' to close the map lookup.");
        }

        pos++; // consume ']'

        if (keyExpr.Type != typeof(string))
        {
            throw new InvalidOperationException(
                $"A map lookup key must be a string, but is {keyExpr.Type.Name}.");
        }

        if (sawNumber)
        {
            return Expression.Call(typeof(ExpressionCompiler), nameof(MapLookupNumber), null,
                Expression.Constant(numberEntries), keyExpr);
        }

        // String-valued (also the empty map: no entries → every lookup is null).
        return Expression.Call(typeof(ExpressionCompiler), nameof(MapLookupString), null,
            Expression.Constant(stringEntries), keyExpr);
    }

    /// <summary>A string-map lookup: the mapped value, or <c>null</c> when the key is missing/null (spec §3.4).</summary>
    private static string? MapLookupString(Dictionary<string, string> map, string? key) =>
        key is not null && map.TryGetValue(key, out string? value) ? value : null;

    /// <summary>A number-map lookup: the mapped value, or <c>null</c> when the key is missing/null (spec §3.4).</summary>
    private static double? MapLookupNumber(Dictionary<string, double> map, string? key) =>
        key is not null && map.TryGetValue(key, out double value) ? value : null;

    private static Expression ParseUnary(Token[] tokens, ref int pos, ExpressionBindings b)
    {
        if (pos < tokens.Length && tokens[pos].Kind == TokenKind.OpNot)
        {
            pos++;
            Expression operand = ParseUnary(tokens, ref pos, b);
            return Expression.Not(operand);
        }

        return ParsePrimary(tokens, ref pos, b);
    }

    private static Expression ResolveDotChain(Expression target, Token[] tokens, ref int pos)
    {
        while (pos < tokens.Length && tokens[pos].Kind == TokenKind.Dot)
        {
            pos++;
            if (pos >= tokens.Length || tokens[pos].Kind != TokenKind.Identifier)
            {
                break;
            }

            string member = tokens[pos].Text;
            pos++;
            PropertyInfo? prop = target.Type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
            if (prop is null)
            {
                throw new InvalidOperationException($"Unknown member: {member} on {target.Type.Name}");
            }

            target = Expression.Property(target, prop);
        }

        return target;
    }

    private static double SafeDivide(double left, double right) => right == 0 ? 0 : left / right;
    private static int SafeDivideInt(int left, int right) => right == 0 ? 0 : left / right;

    private static Token[] Tokenize(string input)
    {
        List<Token> tokens = new();
        int i = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '(')
            {
                tokens.Add(new Token(TokenKind.LParen, "("));
                i++;
                continue;
            }

            if (c == ')')
            {
                tokens.Add(new Token(TokenKind.RParen, ")"));
                i++;
                continue;
            }

            if (c == '[')
            {
                tokens.Add(new Token(TokenKind.LBracket, "["));
                i++;
                continue;
            }

            if (c == ']')
            {
                tokens.Add(new Token(TokenKind.RBracket, "]"));
                i++;
                continue;
            }

            if (c == '{')
            {
                tokens.Add(new Token(TokenKind.LBrace, "{"));
                i++;
                continue;
            }

            if (c == '}')
            {
                tokens.Add(new Token(TokenKind.RBrace, "}"));
                i++;
                continue;
            }

            if (c == ':')
            {
                tokens.Add(new Token(TokenKind.Colon, ":"));
                i++;
                continue;
            }

            if (c == '.')
            {
                tokens.Add(new Token(TokenKind.Dot, "."));
                i++;
                continue;
            }

            if (c == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                i++;
                continue;
            }

            if (c == '+')
            {
                tokens.Add(new Token(TokenKind.OpPlus, "+"));
                i++;
                continue;
            }

            if (c == '-')
            {
                tokens.Add(new Token(TokenKind.OpMinus, "-"));
                i++;
                continue;
            }

            if (c == '*')
            {
                tokens.Add(new Token(TokenKind.OpMul, "*"));
                i++;
                continue;
            }

            if (c == '/')
            {
                tokens.Add(new Token(TokenKind.OpDiv, "/"));
                i++;
                continue;
            }

            if (c == '%')
            {
                tokens.Add(new Token(TokenKind.OpMod, "%"));
                i++;
                continue;
            }

            if (c == '=' && i + 1 < input.Length && input[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.OpEq, "=="));
                i += 2;
                continue;
            }

            if (c == '!' && i + 1 < input.Length && input[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.OpNeq, "!="));
                i += 2;
                continue;
            }

            if (c == '!')
            {
                tokens.Add(new Token(TokenKind.OpNot, "!"));
                i++;
                continue;
            }

            if (c == '>' && i + 1 < input.Length && input[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.OpGte, ">="));
                i += 2;
                continue;
            }

            if (c == '>')
            {
                tokens.Add(new Token(TokenKind.OpGt, ">"));
                i++;
                continue;
            }

            if (c == '<' && i + 1 < input.Length && input[i + 1] == '=')
            {
                tokens.Add(new Token(TokenKind.OpLte, "<="));
                i += 2;
                continue;
            }

            if (c == '<')
            {
                tokens.Add(new Token(TokenKind.OpLt, "<"));
                i++;
                continue;
            }

            if (c == '&' && i + 1 < input.Length && input[i + 1] == '&')
            {
                tokens.Add(new Token(TokenKind.OpAnd, "&&"));
                i += 2;
                continue;
            }

            if (c == '|' && i + 1 < input.Length && input[i + 1] == '|')
            {
                tokens.Add(new Token(TokenKind.OpOr, "||"));
                i += 2;
                continue;
            }

            if (c == '"')
            {
                int start = i;
                i++;
                while (i < input.Length && input[i] != '"')
                {
                    i++;
                }

                if (i < input.Length)
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.String, input[start..i]));
                continue;
            }

            if (char.IsDigit(c))
            {
                int start = i;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Number, input[start..i]));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_'))
                {
                    i++;
                }

                tokens.Add(new Token(TokenKind.Identifier, input[start..i]));
                continue;
            }

            i++;
        }

        return tokens.ToArray();
    }

    // ── Tokenizer ───────────────────────────────────────────────────────────

    private enum TokenKind
    {
        Identifier,
        Number,
        String,
        Dot,
        Comma,
        LParen,
        RParen,
        LBracket,
        RBracket,
        LBrace,
        RBrace,
        Colon,
        OpEq,
        OpNeq,
        OpGt,
        OpGte,
        OpLt,
        OpLte,
        OpPlus,
        OpMinus,
        OpMul,
        OpDiv,
        OpMod,
        OpAnd,
        OpOr,
        OpNot
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    /// <summary>
    ///     Whether a game-event field holds a player-controller slot, and so can anchor an
    ///     <c>.entity.…</c> read.
    /// </summary>
    /// <remarks>
    ///     This used to be a name test — anything ending in <c>Slot</c>. That worked while the
    ///     retired generator renamed <c>userid</c> to <c>VictimSlot</c>/<c>PlayerSlot</c> and
    ///     encoded the semantic in the identifier. The SDK keeps the schema's own names
    ///     (<c>UserId</c>, <c>Attacker</c>, <c>Assister</c>), so the suffix is gone and the
    ///     name carries nothing.
    ///
    ///     The names below are the schema's player-reference fields; CS2 tags them
    ///     <c>player_controller_and_pawn</c> on the wire. The <c>Slot</c> suffix is still accepted
    ///     because the analysis layer's own synthesized events (molotov_thrown) declare
    ///     <c>PlayerSlot</c> directly and are not SDK records.
    /// </remarks>
    public static bool IsPlayerSlotField(string fieldName) =>
        fieldName.EndsWith("Slot", StringComparison.Ordinal)
        || fieldName is "UserId" or "Attacker" or "Assister" or "Victim"
            or "BotId" or "Victimid" or "Attackerid" or "AvengerId" or "AvengedPlayerId";

    /// <summary>
    ///     Type-aware form: a <see cref="uint" /> field is a pawn entity handle, never a slot,
    ///     whatever it is named.
    /// </summary>
    /// <remarks>
    ///     Sdk 4.1 (docs/MIGRATION-4.1.md) exposes the pawn wire keys, and eleven of them sit on
    ///     properties still NAMED <c>UserId</c> — the schema declares those fields
    ///     <c>player_pawn</c>, so there is no controller slot on the wire at all and the name test
    ///     alone would anchor an <c>.entity.…</c> read on a handle value. The SDK types every
    ///     pawn-handle property <c>uint</c> and every slot <c>int</c>/<c>short</c> (widened to
    ///     <c>int</c> by <c>EventFieldAccessor</c>), so the CLR type is the discriminator that
    ///     needs no per-event table. Callers with no type in hand (the ruleset catalog's
    ///     name-only paths) keep the name-only overload.
    /// </remarks>
    public static bool IsPlayerSlotField(string fieldName, Type? fieldType) =>
        fieldType != typeof(uint) && IsPlayerSlotField(fieldName);


    /// <summary>
    ///     Promotes byte/sbyte/short/ushort to int, which expression trees do not do on their own.
    /// </summary>
    /// <remarks>
    ///     The SDK types each event field to its KV1 wire tag, so narrow widths reach the compiler
    ///     where the retired generator emitted int everywhere. Without this, an expression as
    ///     ordinary as <c>event.DmgHealth + event.DmgArmor</c> throws at rule-build time: C# would
    ///     promote both operands, Expression.Add will not.
    /// </remarks>
    private static Expression WidenNarrowIntegral(Expression e) =>
        e.Type == typeof(byte) || e.Type == typeof(sbyte)
        || e.Type == typeof(short) || e.Type == typeof(ushort)
            ? Expression.Convert(e, typeof(int))
            : e;

}
