#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Records the frame-clock tick of the FIRST matching game event of the round into a
///     round-scoped <see cref="ValueNode{Int32}" />, write-once (Approach 1 clip-start): a companion
///     of a <c>count:</c> stat's increment edge, gated by the SAME condition, so it stamps the tick
///     of the first contributing kill (e.g. of a 4K). The count highlight fires at the completing
///     kill; the highlight emission closure reads this node to reach the reel clip window back to
///     the first kill.
///     <para>
///         Write-once is expressed against a <see cref="Sentinel" /> seed value rather than a
///         separate seen-guard: the node is seeded to <see cref="Sentinel" /> and reset to it at
///         each round boundary (round-scoped), and the first matching event overwrites it exactly
///         once per round. A reader treats <see cref="Sentinel" /> as "unset".
///     </para>
/// </summary>
public sealed class RecordFirstEventTickEdge(
    StateNode source,
    ValueNode<int> node,
    Type messageType,
    Delegate? condition) : StateEdge(source)
{
    // The builder compiles event conditions with parameterType: typeof(GameEvent), so the
    // delegate IS a Func<GameEvent, bool>; casting once here replaces the per-fire
    // DynamicInvoke (arg-array + boxed-return allocation, reflection dispatch).
    private readonly Func<GameEvent, bool>? _condition = (Func<GameEvent, bool>?)condition;
    /// <summary>The "unset" seed / reset value; a reader treats this as no first-tick recorded yet.</summary>
    public const int Sentinel = int.MinValue;

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override StateNode? WrittenNode => node;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        GameEvent evt = gem.DecodedEvent;

        // The type gate gates on the PAYLOAD (that is what messageType names), while the compiled
        // condition and the tick below both take the fire — per-fire transport lives on the envelope,
        // so an `event.tick` reference has to reach it. A synthesized event has no payload and is its
        // own subject.
        object subject = evt.Payload ?? evt;
        if (subject.GetType() != messageType)
        {
            return false;
        }

        if (_condition is not null && !_condition(evt))
        {
            return false;
        }

        // Write-once per round: only the first matching event overwrites the sentinel seed.
        if (node.Value == Sentinel)
        {
            node.SetValue(evt.GameTick);
        }

        return true;
    }
}
