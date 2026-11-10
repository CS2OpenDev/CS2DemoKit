#region

using System.Globalization;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A named node that stores a typed value. <see cref="StateNode.IsActive" /> returns
///     <c>true</c> once <see cref="SetValue" /> has been called at least once.
/// </summary>
/// <remarks>
///     Rule chain predicates should inspect <see cref="Value" /> directly:
///     <code>
/// () => mapNode.Value == "de_mirage"
/// () => roundNumber.Value % 2 == 1
/// </code>
///     For boolean state nodes use <see cref="BoolNode" /> instead.
/// </remarks>
/// <typeparam name="T">The type of value stored by this node.</typeparam>
public abstract class ValueNode<T> : StateNode
{
    private string? _cachedDisplayValue;
    private bool _displayDirty = true;
    private bool _hasValue;

    /// <inheritdoc />
    /// <remarks>Becomes <c>true</c> once <see cref="SetValue" /> has been called at least once.</remarks>
    public override bool IsActive => _hasValue;

    /// <summary>The current stored value. Default until <see cref="SetValue" /> is first called.</summary>
    public T Value { get; private set; } = default!;

    /// <inheritdoc />
    public override string? GetDisplayValue()
    {
        if (!IsActive)
        {
            return null;
        }

        if (_displayDirty)
        {
            // Invariant like every explicitly-formatting node type (ComputedStatNode,
            // EntityValuePullNode, …): this string is baked into persisted artifacts
            // (HighlightFired.RenderedTitle → the library-wide highlights cache), so it must
            // not vary by machine locale.
            _cachedDisplayValue = Value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : Value?.ToString();
            _displayDirty = false;
        }

        return _cachedDisplayValue;
    }

    /// <inheritdoc />
    /// <remarks>Numeric value types convert to <see cref="float" />; strings, bools and enums yield <c>null</c>.</remarks>
    public override float? GetNumericValue() => !_hasValue
        ? null
        : Value switch
        {
            int i => i,
            long l => l,
            float f => f,
            double d => (float)d,
            short s => s,
            byte b => b,
            uint u => u,
            ushort us => us,
            sbyte sb => sb,
            _ => null
        };

    /// <summary>Stores a new value and marks the node active. Triggers display-value recomputation on next read.</summary>
    public void SetValue(T value)
    {
        Value = value;
        _hasValue = true;
        _displayDirty = true;
    }

    /// <summary>
    ///     Whether <see cref="SetValue" /> has ever been called. Distinct from <see cref="IsActive" />
    ///     only on subclasses that override activation (<c>BoolNode</c> reports its stored value);
    ///     exposed so match-restart baselines can distinguish "seeded with a default" from "never
    ///     set" and restore the right one.
    /// </summary>
    public bool HasEverBeenSet => _hasValue;

    /// <summary>
    ///     Returns the node to its never-set state: default value, inactive, display cleared. The
    ///     match-restart primitive — a capture-style stat that had no value when the baseline was
    ///     taken must read as UNSET again after a restart, not as an active default
    ///     (<see cref="SetValue" /> latches activation, so restoring via it would fabricate one).
    /// </summary>
    public void ResetToUnset()
    {
        Value = default!;
        _hasValue = false;
        _displayDirty = true;
    }
}
