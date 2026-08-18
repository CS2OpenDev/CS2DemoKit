namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     The complete output of a <c>StateGraphEvaluator</c> run over a demo —
///     a flat, time-ordered list of rule chain satisfaction events.
/// </summary>
/// <param name="Events">All satisfaction events in frame order.</param>
public sealed record RuleChainTimeline(IReadOnlyList<RuleChainEvent> Events)
{
    /// <summary>Returns the number of times the named rule chain was satisfied.</summary>
    public int CountFor(string chainName) =>
        Events.Count(e => e.ChainName == chainName);

    /// <summary>Returns all events for the named rule chain.</summary>
    public IEnumerable<RuleChainEvent> ForChain(string chainName) =>
        Events.Where(e => e.ChainName == chainName);
}
