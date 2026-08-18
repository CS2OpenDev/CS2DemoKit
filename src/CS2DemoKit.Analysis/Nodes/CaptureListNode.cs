#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A round-scoped collection-valued capture node (Rulesets v2 <c>capture: … keep: list</c>):
///     one node holds every captured value of the round as an immutable
///     <c>IReadOnlyList&lt;int&gt;</c> (copy-on-append — the append edge writes <c>old + item</c>,
///     never mutating in place), replacing v1's five copy-pasted <c>pp_kill_tick_N</c> scalar
///     rules. Resets to the shared empty list each round. Its display value is the
///     comma-joined elements (the snapshot contract — the serializer flattens it to
///     <c>&lt;Label&gt;Count</c> + <c>&lt;Label&gt;1..N</c>); an empty round renders <c>null</c>
///     so a no-capture round projects a blank cell, matching v1's null scalar slots.
/// </summary>
public sealed class RoundScopedIntListCaptureNode : RoundScopedValueNode<IReadOnlyList<int>>
{
    /// <summary>Creates the node, defaulting (and per-round resetting) to a shared empty list.</summary>
    /// <param name="name">Unique display name for diagnostics and the timeline.</param>
    /// <param name="subtitle">Optional secondary label (e.g. player name).</param>
    public RoundScopedIntListCaptureNode(string name, string? subtitle = null)
        : base([])
    {
        Name = name;
        Subtitle = subtitle;
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <inheritdoc />
    public override string? GetDisplayValue() => CaptureListNode.Render(Value);

    /// <inheritdoc />
    public override float? GetNumericValue() => Value?.Count;
}

/// <summary>
///     The match-scoped twin of <see cref="RoundScopedIntListCaptureNode" /> (a
///     <c>per: match</c> list capture): accumulates for the whole match, never resetting.
/// </summary>
public sealed class IntListCaptureNode : ValueNode<IReadOnlyList<int>>
{
    /// <summary>Creates the node and seeds it with the shared empty list (active from tick 0).</summary>
    /// <param name="name">Unique display name for diagnostics and the timeline.</param>
    /// <param name="subtitle">Optional secondary label (e.g. player name).</param>
    public IntListCaptureNode(string name, string? subtitle = null)
    {
        Name = name;
        Subtitle = subtitle;
        SetValue([]);
    }

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <inheritdoc />
    public override string? GetDisplayValue() => CaptureListNode.Render(Value);

    /// <inheritdoc />
    public override float? GetNumericValue() => Value?.Count;
}

/// <summary>Shared rendering for the capture-list nodes.</summary>
internal static class CaptureListNode
{
    /// <summary>Renders a capture list as its comma-joined elements, or <c>null</c> when empty.</summary>
    /// <param name="values">The captured values.</param>
    /// <returns>The comma-joined string, or <c>null</c> for an empty/absent list.</returns>
    public static string? Render(IReadOnlyList<int>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        return string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));
    }
}
