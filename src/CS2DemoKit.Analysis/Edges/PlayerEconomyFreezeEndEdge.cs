#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     The B6 relative-economy maintenance edge (docs/rules-v2/rule-authoring-ux-review.md §3.3 risk 1
///     decision c): once per subject at <c>round_freeze_end</c> it writes the subject's
///     <c>round.team.*</c> and <c>round.enemies.*</c> economy nodes (equipment, money) from the
///     digest-sampled per-player values summed per side, so every downstream read is a pure
///     single-node predicate (no walk over all players at read time).
///     <para>
///         Each connected player's value is read through the <see cref="Sum.Read" /> of its sum — in
///         production a closure over the scanner's pre-frame snapshot of <c>entity.pawn.equipment_value</c>
///         or <c>entity.controller.money</c> (the same entity-digest substrate
///         <c>enrich.hurt.victim_health_before</c> uses). There is no driving event to fold this into
///         (unlike the alive counts), so it samples at the freeze-end boundary, matching the corpus's
///         economy-sampling convention: after the buys made during freeze time, before any made in the
///         rest of buy time. Built once per subject; the compute is redundant across teammates but
///         deterministic, matching the per-player template's "each slot materializes its own copy"
///         convention.
///     </para>
///     <para>
///         The subject is a player (the per-player template, whose team is read live at the boundary)
///         or a side (the <c>for: each_team</c> scope, whose side is fixed).
///     </para>
/// </summary>
public sealed class PlayerEconomyFreezeEndEdge : StateEdge
{
    private readonly PlayerContextIndex _playerContext;
    private readonly Func<int> _subjectTeam;
    private readonly IReadOnlyList<Sum> _sums;
    private readonly IReadOnlyList<StateNode> _additional;

    /// <summary>The per-player subject form with the single equipment sum (the original shape).</summary>
    /// <param name="source">The edge source (the graph root).</param>
    /// <param name="playerContext">The shared player-context index.</param>
    /// <param name="readEquipment">Reads a slot's equipment value.</param>
    /// <param name="subjectSlot">The subject player, whose team is read live at the boundary.</param>
    /// <param name="teamEquipment">The subject's <c>round.team.equipment</c> node.</param>
    /// <param name="enemiesEquipment">The subject's <c>round.enemies.equipment</c> node.</param>
    public PlayerEconomyFreezeEndEdge(
        StateNode source,
        PlayerContextIndex playerContext,
        Func<int, int> readEquipment,
        int subjectSlot,
        GenericValueNode<int> teamEquipment,
        GenericValueNode<int> enemiesEquipment)
        : this(source, playerContext, () => playerContext.GetCurrentTeam(subjectSlot),
            [new Sum(readEquipment, teamEquipment, enemiesEquipment)])
    {
    }

    /// <summary>The general form: any number of per-side sums relative to a subject team.</summary>
    /// <param name="source">The edge source (the graph root).</param>
    /// <param name="playerContext">The shared player-context index.</param>
    /// <param name="subjectTeam">
    ///     The subject's side at the boundary (2 or 3): a player's live team, or a fixed side for a
    ///     team subject.
    /// </param>
    /// <param name="sums">The sums to write, at least one.</param>
    public PlayerEconomyFreezeEndEdge(
        StateNode source,
        PlayerContextIndex playerContext,
        Func<int> subjectTeam,
        IReadOnlyList<Sum> sums) : base(source)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
        ArgumentNullException.ThrowIfNull(subjectTeam);
        ArgumentNullException.ThrowIfNull(sums);
        if (sums.Count == 0)
        {
            throw new ArgumentException("an economy edge needs at least one sum to write", nameof(sums));
        }

        _playerContext = playerContext;
        _subjectTeam = subjectTeam;
        _sums = sums;

        List<StateNode> additional = [sums[0].Enemies];
        for (int i = 1; i < sums.Count; i++)
        {
            additional.Add(sums[i].Team);
            additional.Add(sums[i].Enemies);
        }

        _additional = additional;
    }

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes => _additional;

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.SetValue;

    /// <inheritdoc />
    public override Type MessageType => typeof(RoundFreezeEndEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => _sums[0].Team;

    /// <summary>
    ///     The pure summation the edge applies: the total of the connected players on the subject's
    ///     current team and on the opposing team. Disconnected players are excluded (matching the
    ///     Connected-gated alive/count aggregates). Exposed so the economy folding is unit-testable
    ///     without the entity scanner.
    /// </summary>
    /// <param name="playerContext">The shared player-context index.</param>
    /// <param name="subjectSlot">The subject the sums are relative to.</param>
    /// <param name="readEquipment">Reads a slot's current value.</param>
    /// <returns>The (subject-team, opposing-team) sums.</returns>
    public static (int Team, int Enemies) ComputeSums(
        PlayerContextIndex playerContext, int subjectSlot, Func<int, int> readEquipment)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
        return ComputeSideSums(playerContext, playerContext.GetCurrentTeam(subjectSlot), readEquipment);
    }

    /// <summary>
    ///     <see cref="ComputeSums" /> for a subject side rather than a subject player: the connected
    ///     players on <paramref name="subjectTeam" />, and on the other side.
    /// </summary>
    /// <param name="playerContext">The shared player-context index.</param>
    /// <param name="subjectTeam">The subject side (2 or 3).</param>
    /// <param name="read">Reads a slot's current value.</param>
    /// <returns>The (subject-side, opposing-side) sums.</returns>
    public static (int Team, int Enemies) ComputeSideSums(
        PlayerContextIndex playerContext, int subjectTeam, Func<int, int> read)
    {
        ArgumentNullException.ThrowIfNull(playerContext);
        ArgumentNullException.ThrowIfNull(read);

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
                teamSum += read(ctx.Slot);
            }
            else if (ctx.Team == enemyTeam)
            {
                enemySum += read(ctx.Slot);
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

        int subjectTeam = _subjectTeam();
        foreach (Sum sum in _sums)
        {
            (int team, int enemies) = ComputeSideSums(_playerContext, subjectTeam, sum.Read);
            sum.Team.SetValue(team);
            sum.Enemies.SetValue(enemies);
        }

        return true;
    }

    /// <summary>One per-side sum the edge writes: the per-player reader and the two nodes it fills.</summary>
    /// <param name="Read">Reads one player's value by slot.</param>
    /// <param name="Team">The node for the subject side's sum (<c>round.team.*</c>).</param>
    /// <param name="Enemies">The node for the other side's sum (<c>round.enemies.*</c>).</param>
    public readonly record struct Sum(Func<int, int> Read, GenericValueNode<int> Team, GenericValueNode<int> Enemies);
}
