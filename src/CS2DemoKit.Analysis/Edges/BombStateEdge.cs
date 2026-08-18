#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Updates a player's <c>Team</c> in <see cref="PlayerContextIndex" /> when a
///     <c>player_team</c> event fires (notably at halftime). Without this, all
///     players' teams would remain frozen at the team value seen at materialization,
///     which would silently break side-aware stats (CT/T wins, etc.) for the second
///     half of a match.
/// </summary>
public sealed class PlayerTeamEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerTeamEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerTeamEvent pt)
        {
            return false;
        }

        if (ctx.TryGet(pt.UserId, out PlayerContextIndex.PlayerContext? pCtx) && pCtx is not null)
        {
            pCtx.Team = pt.Team;
        }

        return false;
    }
}

/// <summary>Sets <see cref="PlayerContextIndex.BombPlanted" /> on <c>bomb_planted</c>.</summary>
public sealed class BombPlantedEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(BombPlantedEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not BombPlantedEvent)
        {
            return false;
        }

        ctx.BombPlanted = true;
        return false;
    }
}

/// <summary>Sets <see cref="PlayerContextIndex.BombDefused" /> on <c>bomb_defused</c>.</summary>
public sealed class BombDefusedEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(BombDefusedEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not BombDefusedEvent)
        {
            return false;
        }

        ctx.BombDefused = true;
        return false;
    }
}

/// <summary>Sets <see cref="PlayerContextIndex.BombExploded" /> on <c>bomb_exploded</c>.</summary>
public sealed class BombExplodedEdge(StateNode source, PlayerContextIndex ctx) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => null;

    /// <inheritdoc />
    public override Type MessageType => typeof(BombExplodedEvent);

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not BombExplodedEvent)
        {
            return false;
        }

        ctx.BombExploded = true;
        return false;
    }
}
