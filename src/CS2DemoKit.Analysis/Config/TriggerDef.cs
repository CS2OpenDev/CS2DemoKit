namespace CS2DemoKit.Analysis.Config;

/// <summary>An event-driven trigger inside a <see cref="RuleDef" />: which event fires it, and what action it performs.</summary>
/// <param name="On">Event name (concrete game event, net message, logical alias, or context channel).</param>
/// <param name="Action">Effect applied to the rule's node when the trigger fires.</param>
/// <param name="Condition">Optional predicate expression filtering the event payload.</param>
/// <param name="Value">For Set/Increment actions: expression yielding the new/delta value.</param>
public sealed record TriggerDef(
    string On,
    TriggerAction Action = TriggerAction.Activate,
    string? Condition = null,
    string? Value = null);

/// <summary>Effect a trigger applies to its rule's node when its condition is satisfied.</summary>
public enum TriggerAction
{
    /// <summary>Set the bool node to true.</summary>
    Activate,

    /// <summary>Set the bool node to false.</summary>
    Deactivate,

    /// <summary>Add to the counter rule's stored value.</summary>
    Increment,

    /// <summary>Replace the value rule's stored value with the trigger's value expression.</summary>
    Set,

    /// <summary>
    ///     KeyedCounter rules only: add the trigger's value expression to the bucket selected by the
    ///     rule's <c>key:</c> expression. (Plain rules accumulate via <c>set</c> +
    ///     <c>rule.value + …</c> instead — <c>add</c> on a non-keyed rule is a load/build error.)
    /// </summary>
    Add
}
