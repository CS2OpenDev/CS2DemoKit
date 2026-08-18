#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     A per-player B6 team-aggregate node whose integer <see cref="Value" /> is recomputed live from
///     <see cref="PlayerContextIndex" /> on every read — the single-writer design
///     (docs/rules-v2/rule-authoring-ux-review.md §3.3 risk 1 decision a): the alive index is mutated by
///     exactly one writer (the <c>MarkDead</c> edge, plus round reset / connectivity), and these
///     aggregates are pure derivations of that index. There is deliberately NO second incremental
///     store to drift out of sync.
///     <para>
///         The value is relative to the SUBJECT (the slot this node was materialized for): the
///         subject's current team is read live via <see cref="PlayerContextIndex.GetCurrentTeam" />
///         (so a halftime side-swap is reflected), and the enemy side is its complement. Because it
///         reflects the index at read time, a read during a <c>player_death</c> dispatch sees deaths
///         up to and including the current one — the same post-<c>MarkDead</c> ordering clutch
///         detection already relies on. Excluded from snapshots (<see cref="ISnapshotExcludedNode" />):
///         a derived context value, invisible in output.
///     </para>
/// </summary>
public sealed class RoundTeamAggregateNode : StateNode, ISnapshotExcludedNode
{
    /// <summary>Which side + population the aggregate reports, relative to the subject.</summary>
    public enum AggregateKind
    {
        /// <summary>Alive+connected players on the subject's team (<c>round.team.alive</c>).</summary>
        TeamAlive,

        /// <summary>Alive+connected players on the opposing team (<c>round.enemies.alive</c>).</summary>
        EnemyAlive,

        /// <summary>Connected players on the subject's team (<c>round.team.players</c>).</summary>
        TeamPlayers,

        /// <summary>Connected players on the opposing team (<c>round.enemies.players</c>).</summary>
        EnemyPlayers
    }

    private readonly PlayerContextIndex _index;
    private readonly AggregateKind _kind;
    private readonly int _slot;

    /// <summary>Creates a per-subject team aggregate.</summary>
    /// <param name="name">The node's unique name (the B6 v1 rule id, e.g. <c>round_team_alive</c>).</param>
    /// <param name="index">The shared player-context index the aggregate derives from.</param>
    /// <param name="slot">The subject player slot this aggregate is relative to.</param>
    /// <param name="kind">Which side + population to report.</param>
    /// <param name="subtitle">Optional display subtitle (the player name).</param>
    public RoundTeamAggregateNode(string name, PlayerContextIndex index, int slot, AggregateKind kind, string? subtitle = null)
    {
        Name = name;
        _index = index;
        _slot = slot;
        _kind = kind;
        Subtitle = subtitle;
    }

    /// <inheritdoc />
    public override bool IsActive => true;

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <summary>The live aggregate value, read reflectively by the expression compiler.</summary>
    public int Value => Compute();

    /// <inheritdoc />
    public override string? GetDisplayValue() => Compute().ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override float? GetNumericValue() => Compute();

    private int Compute()
    {
        int team = _index.GetCurrentTeam(_slot);
        int enemy = team switch
        {
            2 => 3,
            3 => 2,
            _ => 0
        };

        return _kind switch
        {
            AggregateKind.TeamAlive => _index.CountAlive(team),
            AggregateKind.EnemyAlive => _index.CountAlive(enemy),
            AggregateKind.TeamPlayers => _index.CountConnected(team),
            AggregateKind.EnemyPlayers => _index.CountConnected(enemy),
            _ => 0
        };
    }
}

/// <summary>
///     A per-player B6 clutch facet (<c>round.alive.in_clutch</c>): the existing clutch enrichment
///     exposed as a typed boolean facet. Its <see cref="Value" /> is <c>true</c> while the subject is
///     the lone connected survivor in an active 1vN clutch — read live from
///     <see cref="PlayerContextIndex" /> (the <c>IsInClutch</c> flag the clutch enrichment edge sets),
///     Connected-gated so a disconnected ghost never reports a clutch. Excluded from snapshots.
/// </summary>
public sealed class RoundClutchFacetNode : StateNode, ISnapshotExcludedNode
{
    private readonly PlayerContextIndex _index;
    private readonly int _slot;

    /// <summary>Creates the clutch facet for a subject slot.</summary>
    /// <param name="name">The node's unique name (the B6 v1 rule id, <c>round_alive_in_clutch</c>).</param>
    /// <param name="index">The shared player-context index.</param>
    /// <param name="slot">The subject player slot.</param>
    /// <param name="subtitle">Optional display subtitle (the player name).</param>
    public RoundClutchFacetNode(string name, PlayerContextIndex index, int slot, string? subtitle = null)
    {
        Name = name;
        _index = index;
        _slot = slot;
        Subtitle = subtitle;
    }

    /// <inheritdoc />
    public override bool IsActive => true;

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <summary>The live clutch flag, read reflectively by the expression compiler.</summary>
    public bool Value =>
        _index.TryGet(_slot, out PlayerContextIndex.PlayerContext? ctx)
        && ctx is { Connected: true, IsInClutch: true };
}

/// <summary>
///     A per-player B6 clutch-size facet (<c>round.clutch.size</c>): the number of enemies alive when
///     the subject ENTERED their clutch (the N of a 1vN), or 0 when the subject is not the clutcher.
///     Read live from <see cref="PlayerContextIndex" /> (the <c>ClutchOpponents</c> value the clutch
///     enrichment edge captures at clutch entry), Connected-gated. Held for the round, so a WON clutch
///     still reports N at round end — exactly when a <c>enrich.clutch.was_clutch_won</c> gate fires.
///     Excluded from snapshots.
/// </summary>
public sealed class RoundClutchSizeNode : StateNode, ISnapshotExcludedNode
{
    private readonly PlayerContextIndex _index;
    private readonly int _slot;

    /// <summary>Creates the clutch-size facet for a subject slot.</summary>
    /// <param name="name">The node's unique name (the B6 v1 rule id, <c>round_clutch_size</c>).</param>
    /// <param name="index">The shared player-context index.</param>
    /// <param name="slot">The subject player slot.</param>
    /// <param name="subtitle">Optional display subtitle (the player name).</param>
    public RoundClutchSizeNode(string name, PlayerContextIndex index, int slot, string? subtitle = null)
    {
        Name = name;
        _index = index;
        _slot = slot;
        Subtitle = subtitle;
    }

    /// <inheritdoc />
    public override bool IsActive => true;

    /// <inheritdoc />
    public override string Name { get; }

    /// <inheritdoc />
    public override string? Subtitle { get; }

    /// <summary>The live clutch size (N), read reflectively by the expression compiler; 0 when not clutching.</summary>
    public int Value =>
        _index.TryGet(_slot, out PlayerContextIndex.PlayerContext? ctx) && ctx is { Connected: true }
            ? ctx.ClutchOpponents
            : 0;
}
