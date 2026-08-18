namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     A named <see cref="BoolNode" /> that is active when ANY of its <see cref="Inputs" /> is
///     satisfied. The OR counterpart to <see cref="ConjunctionNode" /> (AND).
/// </summary>
/// <param name="name">Unique display name shown in diagnostics and the rule chain timeline.</param>
/// <param name="inputs">The conditional edges whose satisfaction activates this node.</param>
public sealed class DisjunctionNode(string name, params IConditionalEdge[] inputs) : BoolNode
{
    private bool _inputsDirty = true;
    private bool _wasSatisfied;

    /// <summary>The conditional edges that drive this node; any one satisfied input activates it.</summary>
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
    ///     Returns <c>true</c> exactly on the rising edge — the first call where the disjunction
    ///     transitions from unsatisfied to satisfied.
    /// </summary>
    public bool Recompute()
    {
        if (!_inputsDirty)
        {
            return false;
        }

        _inputsDirty = false;

        bool satisfied = Inputs.Any(e => e.IsSatisfied);
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
