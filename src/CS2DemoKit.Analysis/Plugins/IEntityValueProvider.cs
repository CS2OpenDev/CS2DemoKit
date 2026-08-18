#region

using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Which entity-value transitions an <see cref="IEntityValueProvider" /> wants synthesized
///     as change events. Bool providers default to <see cref="RisingOnly" /> to sidestep
///     entry/exit-race scenarios at round boundaries.
/// </summary>
public enum ChangeDirection
{
    RisingOnly,
    FallingOnly,
    Both
}

/// <summary>
///     Reads a single networked entity field and exposes it to the rule engine as a named
///     context value. The <c>EntityChangeScanner</c> polls every registered provider
///     once per frame, compares the value to its previous read, and emits a synthesized
///     <see cref="EntityValueChangedEvent{TMarker}" /> when the transition matches
///     <see cref="EmitOn" />.
///     <para>
///         Providers expose two outputs:
///         <list type="bullet">
///             <item>
///                 A <c>GenericValueNode&lt;T&gt;</c> in <c>_enrichmentNodes</c> keyed by
///                 <see cref="ContextName" /> — read by rule conditions via the existing
///                 <c>ExpressionCompiler</c> fallback path. The scanner writes the node on every
///                 change regardless of <see cref="EmitOn" />.
///             </item>
///             <item>
///                 A synthesized change event on the dispatch key
///                 <c>typeof(EntityValueChangedEvent&lt;TMarker&gt;)</c> — fires rule edges
///                 keyed on that type. Emitted only on matching transitions.
///             </item>
///         </list>
///     </para>
/// </summary>
public interface IEntityValueProvider
{
    /// <summary>
    ///     Name used in rule YAML/built-in contexts to reference this provider's value, e.g.
    ///     <c>entity.game.freeze_period</c>. The lazy-activation pre-scan in
    ///     <c>RuleChainBuilder.Build()</c> substring-matches this against every rule's
    ///     <c>On</c>, <c>Condition</c>, <c>Value</c>, and <c>When</c> fields to decide whether
    ///     the provider activates.
    /// </summary>
    string ContextName { get; }

    /// <summary>
    ///     Default value seeded into the provider's value node before any frames have been
    ///     processed. Lets condition expressions evaluate against a defined value when the
    ///     entity has not yet spawned.
    /// </summary>
    object? DefaultValue { get; }

    /// <summary>
    ///     Direction of change that triggers a synthesized event. Defaults to
    ///     <see cref="ChangeDirection.RisingOnly" /> for bool fields to sidestep the
    ///     same-tick race between the falling edge and the existing <c>round_freeze_end</c>
    ///     trigger.
    /// </summary>
    ChangeDirection EmitOn { get; }

    /// <summary>The CS2 entity class name to read from (e.g. <c>"CCSGameRules"</c>).</summary>
    string EntityClass { get; }

    /// <summary>The native field path on the entity, e.g. <c>SchemaNames.CCSGameRules.FreezePeriod</c>.</summary>
    string FieldName { get; }

    /// <summary>
    ///     The empty marker type that parameterises <see cref="EntityValueChangedEvent{TMarker}" />
    ///     for this provider. Must be unique per provider — it is the dispatch key consumers
    ///     subscribe against.
    /// </summary>
    Type MarkerType { get; }

    /// <summary>The runtime C# type of the value (e.g. <c>typeof(bool)</c>).</summary>
    Type ValueType { get; }

    /// <summary>
    ///     Reads the current value from the layer's tracker, or returns <c>null</c> if the
    ///     declared entity does not yet exist (pre-spawn) or has been removed.
    /// </summary>
    object? Read(EntityStateLayer layer);
}
