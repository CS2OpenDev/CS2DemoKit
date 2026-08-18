namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Determines how multiple concrete events bound to the same logical event
///     are dispatched per round-scope.
/// </summary>
public enum LogicalEventSemantics
{
    /// <summary>
    ///     The first concrete event in the binding's list that fires within
    ///     a round-scope wins. Later fallbacks for the same round are
    ///     suppressed by a per-round bool guard.
    /// </summary>
    /// <remarks>
    ///     Used for round/match boundary events that are conceptually
    ///     "this round ends now" — only the first such marker should drive
    ///     state transitions, even if subsequent markers also fire.
    /// </remarks>
    FirstWins,

    /// <summary>
    ///     Every concrete event in the binding's list fires every time it
    ///     occurs. No suppression. Used for events that are truly multiple
    ///     occurrences of the same logical concept (e.g. several teamkill
    ///     event flavours that should all count).
    /// </summary>
    AllFire
}

/// <summary>
///     Maps a logical event (e.g. <c>round_end</c>) to an ordered list of
///     concrete game-event names that realise it on a particular demo source.
///     Returned from properties on <see cref="DemoSourceProfile" />; resolved
///     at evaluator-build time by the rule chain builder, never at runtime.
/// </summary>
/// <param name="ConcreteEventNames">
///     Ordered list of concrete event names this logical event resolves to.
///     Must be non-empty when a binding is supplied.
/// </param>
/// <param name="Semantics">
///     How to handle multiple bindings firing within the same round-scope.
///     Defaults to <see cref="LogicalEventSemantics.FirstWins" />.
/// </param>
public sealed record LogicalEventBinding(
    IReadOnlyList<string> ConcreteEventNames,
    LogicalEventSemantics Semantics = LogicalEventSemantics.FirstWins)
{
    /// <summary>
    ///     Convenience factory for events that should all fire whenever they
    ///     occur (no suppression).
    /// </summary>
    public static LogicalEventBinding AllFire(params string[] eventNames) =>
        new(eventNames, LogicalEventSemantics.AllFire);

    /// <summary>
    ///     Convenience factory for an ordered fallback list — first event
    ///     to fire in a round wins.
    /// </summary>
    public static LogicalEventBinding FirstWins(params string[] eventNames) =>
        new(eventNames);

    /// <summary>Convenience factory for a single-event binding.</summary>
    public static LogicalEventBinding Of(string eventName) =>
        new([eventName]);
}
