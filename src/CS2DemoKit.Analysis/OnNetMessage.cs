#region

using CS2DemoKit.Analysis.Abstractions;
using Google.Protobuf;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     A concrete <see cref="NetMessageEdge{TPayload}" /> whose condition is an optional predicate.
///     Use this for simple edges that activate or deactivate a bool node when a specific net
///     message payload arrives, optionally filtered by a predicate on the payload.
/// </summary>
/// <typeparam name="TPayload">The protobuf payload type that triggers this edge.</typeparam>
/// <example>
///     <code>
/// // Activate isDeMirage when the file header says de_mirage
/// new OnNetMessage&lt;CDemoFileHeader&gt;(
///     mapNameNode, isDeMirageNode, EdgeEffect.Activate,
///     hdr => string.Equals(hdr.MapName, "de_mirage", StringComparison.OrdinalIgnoreCase))
/// </code>
/// </example>
/// <param name="source">Must be active for this edge to be eligible.</param>
/// <param name="destination">Receives <paramref name="effect" /> when condition is met.</param>
/// <param name="effect">Activate or Deactivate the destination node.</param>
/// <param name="condition">
///     Optional predicate — if null, any payload of <typeparamref name="TPayload" /> satisfies the
///     edge.
/// </param>
/// <param name="suppressionGuard">
///     Optional first-wins-per-round guard. When non-null, the edge fires only if the guard
///     is inactive; on a successful fire it activates the guard. Use a round-scoped bool node
///     so the guard auto-resets at round boundaries.
/// </param>
public sealed class OnNetMessage<TPayload>(
    StateNode source,
    BoolNode destination,
    EdgeEffect effect,
    Func<TPayload, bool>? condition = null,
    BoolNode? suppressionGuard = null) : NetMessageEdge<TPayload>(source, destination, effect) where TPayload : IMessage
{
    /// <inheritdoc />
    protected override bool Evaluate(EvaluationContext context, TPayload payload)
    {
        if (suppressionGuard?.IsActive == true)
        {
            return false;
        }

        return condition is null || condition(payload);
    }

    /// <inheritdoc />
    protected override void OnAppliedSuccessfully(EvaluationContext context) =>
        suppressionGuard?.Activate();
}
