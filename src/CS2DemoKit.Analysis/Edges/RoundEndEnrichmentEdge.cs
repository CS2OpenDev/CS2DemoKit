#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Enrichment edge that fires on a round-end event (one instance per concrete
///     event in the active profile's <c>$round_end</c> binding) and derives the
///     winning side from bomb state and alive counts. Because CS2's internal
///     team_num IS the side (2=T, 3=CT) and players' team_num swaps at halftime
///     via <c>player_team</c> events, the "winner team" the YAML rules compare
///     against is simply the winning side number. Idempotent: writes the same
///     transient values regardless of which concrete event triggers it, so no
///     first-wins guard is needed.
/// </summary>
public sealed class RoundEndEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode hasWinner,
    TransientValueNode<int> winnerTeam,
    TransientValueNode<int> winnerSide,
    Type messageType) : StateEdge(source)
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [winnerTeam, winnerSide];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => messageType;

    /// <inheritdoc />
    public override StateNode? WrittenNode => hasWinner;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        // A round-end marker that arrives before the FIRST round_freeze_end is the pre-match
        // restart, not the end of a round — there is no round for it to decide. Left ungated it
        // manufactures one: DeriveWinningSide falls through to CT when nobody is alive and no bomb
        // event has happened, which is exactly the pre-match state. Measured on a demo that
        // includes its pre-match period (vitality-vs-fut-m1: cs_pre_restart at tick 307, round 1's
        // freeze-end at 4343): one phantom CT round win, scoring a 19-round match 14–6 over "20"
        // rounds. RoundNumber is incremented by HealthResetEdge on every round_freeze_end, so 0
        // means no round has started. Profiles whose per-round marker only ever fires inside a
        // round (Valve GOTV's round_officially_ended) are unaffected.
        if (playerContext.RoundNumber == 0)
        {
            return false;
        }

        int winningSide = DeriveWinningSide();
        if (winningSide != 2 && winningSide != 3)
        {
            return false;
        }

        hasWinner.Activate();
        winnerTeam.SetValue(winningSide);
        winnerSide.SetValue(winningSide);
        return true;
    }

    /// <summary>
    ///     Derives the winning SIDE (2=T, 3=CT) from bomb state and alive counts.
    ///     Bomb explosion → T win; bomb defusal → CT win; otherwise the side with
    ///     surviving players wins; otherwise time expired = CT win.
    /// </summary>
    private int DeriveWinningSide()
    {
        if (playerContext.BombExploded)
        {
            return 2;
        }

        if (playerContext.BombDefused)
        {
            return 3;
        }

        int aliveT = playerContext.CountAlive(2);
        int aliveCt = playerContext.CountAlive(3);

        if (aliveT > 0 && aliveCt == 0)
        {
            return 2;
        }

        if (aliveCt > 0 && aliveT == 0)
        {
            return 3;
        }

        return 3;
    }
}
