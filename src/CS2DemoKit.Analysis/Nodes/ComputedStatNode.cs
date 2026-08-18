#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A <see cref="ValueNode{Double}" /> that derives its value from a caller-supplied
///     compute delegate. The rule builder calls <see cref="Recompute" /> at round end
///     (and other configured trigger points) to refresh the stored stat value.
/// </summary>
public sealed class ComputedStatNode : ValueNode<double>
{
    private readonly Func<double> _compute;
    private readonly string _format;

    /// <param name="name">Unique display name for diagnostics and the rule chain timeline.</param>
    /// <param name="subtitle">Optional secondary label (e.g. player name) displayed below the name.</param>
    /// <param name="compute">Delegate that computes the current stat value when <see cref="Recompute" /> is called.</param>
    /// <param name="format">.NET numeric format string applied when rendering the value for display.</param>
    public ComputedStatNode(string name, string? subtitle, Func<double> compute, string format = "F1")
    {
        Name = name;
        Subtitle = subtitle;
        _compute = compute;
        _format = format;
        SetValue(0);
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <inheritdoc />
    public override string? GetDisplayValue()
    {
        if (!IsActive)
        {
            return null;
        }

        return Value.ToString(_format, CultureInfo.InvariantCulture);
    }

    /// <summary>Invokes the compute delegate and stores the result as this node's current value.</summary>
    public void Recompute() => SetValue(_compute());
}
