namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A named <see cref="BoolNode" /> that is active when all of its <see cref="Inputs" /> are
///     simultaneously satisfied. Replaces the old <c>RuleChain</c> concept — the node itself
///     carries both the conjunction state and the rising-edge detection used for timeline events.
/// </summary>
/// <remarks>
///     <para>
///         Call <see cref="Recompute" /> after the message-edge pass for each message. The node
///         activates or deactivates itself based on the current satisfaction of all inputs and returns
///         <c>true</c> exactly on the rising edge (first call where all conditions become satisfied
///         after previously being unsatisfied).
///     </para>
///     <para>
///         Construct inputs using the <see cref="ConditionalEdge" /> factory:
///         <code>
/// var node = new ConjunctionNode("IsOddRoundOnMirage",
///     ConditionalEdge.From(mapNameNode,     (string v) => v == "de_mirage", "== \"de_mirage\""),
///     ConditionalEdge.From(roundActiveNode,                                  "active"),
///     ConditionalEdge.From(roundNumberNode, (int    v) => v % 2 == 1,        "% 2 == 1"));
/// </code>
///     </para>
/// </remarks>
/// <param name="name">Unique display name shown in diagnostics and the rule chain timeline.</param>
/// <param name="inputs">The conditional edges whose simultaneous satisfaction activates this node.</param>
public sealed class ConjunctionNode(string name, params IConditionalEdge[] inputs) : BoolNode
{
    private bool _inputsDirty = true;
    private bool _wasSatisfied;

    /// <summary>The conditional edges that must all be satisfied for this node to be active.</summary>
    public IReadOnlyList<IConditionalEdge> Inputs { get; } = inputs;

    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <summary>
    ///     Marks the node's input state as stale so the next <see cref="Recompute" /> re-evaluates
    ///     all inputs. The evaluator calls this when an upstream node that feeds an input changes.
    /// </summary>
    public void MarkInputsDirty() => _inputsDirty = true;

    /// <summary>
    ///     Re-evaluates all inputs (if dirty) and activates or deactivates this node accordingly.
    ///     Returns <c>true</c> exactly on the rising edge — the first call where all inputs
    ///     transition from unsatisfied to satisfied.
    /// </summary>
    public bool Recompute()
    {
        if (!_inputsDirty)
        {
            return false;
        }

        _inputsDirty = false;

        bool satisfied = Inputs.All(e => e.IsSatisfied);
        bool risingEdge = satisfied && !_wasSatisfied;
        _wasSatisfied = satisfied;

        if (satisfied)
        {
            Activate();
        }
        else
        {
            Deactivate();
        }

        return risingEdge;
    }
}
