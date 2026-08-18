#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     The B6 relative-economy maintenance edge (docs/rules-v2/rule-authoring-ux-review.md §3.3 risk 1
///     decision c): once per player at <c>round_freeze_end</c> it writes the subject's
///     <c>round.team.equipment</c> and <c>round.enemies.equipment</c> nodes from the digest-sampled
///     absolute team economy sums plus the subject's team, so every downstream read is a pure
///     single-node predicate (no walk over all players at read time).
///     <para>
///         Each connected player's equipment is read through <paramref name="readEquipment" /> — in
///         production a closure over the scanner's pre-frame <c>entity.pawn.equipment_value</c>
///         snapshot (the same entity-digest substrate <c>enrich.hurt.victim_health_before</c> uses).
///         There is no driving event to fold this into (unlike the alive counts), so it samples at the
///         freeze-end boundary, matching the corpus's economy-sampling convention. Built once per slot;
///         the per-slot compute is redundant across teammates but deterministic, matching the
///         per-player template's "each slot materializes its own copy" convention.
///     </para>
/// </summary>
public sealed class PlayerEconomyFreezeEndEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    Func<int, int> readEquipment,
    int subjectSlot,
    GenericValueNode<int> teamEquipment,
    GenericValueNode<int> enemiesEquipment) : StateEdge(source)
{
    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => [enemiesEquipment];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(RoundFreezeEndEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => teamEquipment;

    /// <summary>
    ///     The pure summation the edge applies: total equipment of the connected players on the
    ///     subject's current team and on the opposing team. Disconnected players are excluded (matching
    ///     the Connected-gated alive/count aggregates). Exposed so the economy folding is unit-testable
    ///     without the entity scanner.
    /// </summary>
    /// <param name="playerContext">The shared player-context index.</param>
    /// <param name="subjectSlot">The subject the sums are relative to.</param>
    /// <param name="readEquipment">Reads a slot's current equipment value.</param>
    /// <returns>The (subject-team, opposing-team) equipment sums.</returns>
    public static (int Team, int Enemies) ComputeSums(
        PlayerContextIndex playerContext, int subjectSlot, Func<int, int> readEquipment)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
        ArgumentNullException.ThrowIfNull(readEquipment);

        int subjectTeam = playerContext.GetCurrentTeam(subjectSlot);
        int enemyTeam = subjectTeam switch
        {
            2 => 3,
            3 => 2,
            _ => 0
        };

        int teamSum = 0;
        int enemySum = 0;
        foreach (PlayerContextIndex.PlayerContext ctx in playerContext.AllPlayers)
        {
            if (!ctx.Connected)
            {
                continue;
            }

            if (ctx.Team == subjectTeam)
            {
                teamSum += readEquipment(ctx.Slot);
            }
            else if (ctx.Team == enemyTeam)
            {
                enemySum += readEquipment(ctx.Slot);
            }
        }

        return (teamSum, enemySum);
    }

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem || gem.DecodedEvent.Payload is not RoundFreezeEndEvent)
        {
            return false;
        }

        (int team, int enemies) = ComputeSums(playerContext, subjectSlot, readEquipment);
        teamEquipment.SetValue(team);
        enemiesEquipment.SetValue(enemies);
        return true;
    }
}
