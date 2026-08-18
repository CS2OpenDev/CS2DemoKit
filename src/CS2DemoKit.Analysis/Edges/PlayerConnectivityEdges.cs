#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Clears <see cref="PlayerContextIndex.PlayerContext.Connected" /> when a
///     <c>player_disconnect</c> event fires. This is the write side of the disconnect-ghost defect
///     fix: <see cref="PlayerContextIndex.ResetRoundState" /> resurrects every registered player's
///     <c>IsAlive</c> flag each round but deliberately leaves <c>Connected</c> untouched, so a
///     mid-match disconnect stops inflating alive counts (and clutch detection) in later rounds.
/// </summary>
public sealed class PlayerDisconnectEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerDisconnectEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerDisconnectEvent disconnect)
        {
            return false;
        }

        ctx.MarkDisconnected(disconnect.UserId);
        return false;
    }
}

/// <summary>
///     Sets <see cref="PlayerContextIndex.PlayerContext.Connected" /> on <c>player_connect</c>.
///     Registration already defaults <c>Connected</c> to <c>true</c>, so this only matters when a
///     slot that previously disconnected reconnects — re-enabling it in the Connected-gated
///     aggregates.
/// </summary>
public sealed class PlayerConnectEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerConnectEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerConnectEvent connect)
        {
            return false;
        }

        ctx.MarkConnected(connect.UserId);
        return false;
    }
}

/// <summary>
///     Sets <see cref="PlayerContextIndex.PlayerContext.Connected" /> on <c>player_spawn</c>. A spawn
///     is unambiguous evidence the player is present, so it re-marks a slot connected (idempotent for
///     already-connected players).
/// </summary>
public sealed class PlayerSpawnConnectivityEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerSpawnEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerSpawnEvent spawn)
        {
            return false;
        }

        ctx.MarkConnected(spawn.UserId);
        return false;
    }
}
