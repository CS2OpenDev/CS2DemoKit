#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Observation;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     Declares the nodes and edges of every <c>for: each_projectile</c> ruleset at graph-build
///     time. The evaluator calls <see cref="Materialize" /> on each <c>projectile_created</c>, keeps
///     the instance while its projectile lives, and on <c>projectile_removed</c> samples its nodes
///     into a <see cref="ProjectileRecord" /> and releases its edges.
/// </summary>
public sealed class ProjectileNodeTemplate(Func<ProjectileRow, ProjectileNodeTemplate.MaterializedProjectile> factory)
{
    /// <summary>Produces one instance's nodes and edges for the projectile <paramref name="row" /> describes.</summary>
    /// <param name="row">The projectile's row on the frame its <c>projectile_created</c> fires, thrower resolved.</param>
    public MaterializedProjectile Materialize(ProjectileRow row) => factory(row);

    /// <summary>The materialization output for one projectile.</summary>
    /// <param name="EntityIndex">The projectile's entity index.</param>
    /// <param name="Serial">The entity serial; with <paramref name="EntityIndex" /> it names the instance.</param>
    /// <param name="ClassName">The projectile's server class.</param>
    /// <param name="ThrowerSlot">The thrower's player slot, 0-63.</param>
    /// <param name="Nodes">The instance's nodes: its stats, their guards, and the pull nodes a stat is.</param>
    /// <param name="Edges">The instance's edges, each bound to the instance by entity index and serial.</param>
    /// <param name="EdgeDescriptors">The wiring rows of <paramref name="Edges" />, as the per-player template's.</param>
    /// <param name="NodesByRuleId">The instance's qualified <c>{ruleset}.{stat}</c> node map, the columns a table samples.</param>
    /// <param name="RisingEdgeActions">Rising-edge callbacks against the instance's own trigger nodes; null when none.</param>
    public readonly record struct MaterializedProjectile(
        int EntityIndex,
        int Serial,
        string ClassName,
        int ThrowerSlot,
        IReadOnlyList<StateNode> Nodes,
        IReadOnlyList<StateEdge> Edges,
        IReadOnlyList<GraphEdgeDescriptor> EdgeDescriptors,
        IReadOnlyDictionary<string, StateNode> NodesByRuleId,
        IReadOnlyList<(StateNode Trigger, Action Action, StateNode? Writes)>? RisingEdgeActions = null);
}

/// <summary>
///     One finished projectile instance: the row a <c>per: projectile</c> table projects. Recorded
///     by the evaluator on <c>projectile_removed</c>, or at the end of the run for a projectile
///     still live, on either run path.
/// </summary>
/// <param name="EntityIndex">The projectile's entity index.</param>
/// <param name="Serial">The entity serial.</param>
/// <param name="ClassName">The projectile's server class.</param>
/// <param name="ThrowerSlot">The thrower's player slot, 0-63.</param>
/// <param name="ThrowerName">The thrower's name as the run knew it.</param>
/// <param name="Team">The projectile's <c>m_iTeamNum</c> when it was created (2 = T, 3 = CT), or -1 when unseen.</param>
/// <param name="CreatedTick">The frame-clock tick <c>projectile_created</c> fired on.</param>
/// <param name="RemovedTick">The frame-clock tick <c>projectile_removed</c> fired on, or the run's last tick.</param>
/// <param name="RoundNumber">The live round number when the row was recorded, or 0 when none was live.</param>
/// <param name="Removed">False when the run ended before the projectile was removed.</param>
/// <param name="Values">
///     Each stat's value at recording, keyed by its qualified <c>{ruleset}.{stat}</c> id, read the
///     way a snapshot cell is: null for an inactive node, <c>true</c> for an active flag, else the
///     parsed display value.
/// </param>
public sealed record ProjectileRecord(
    int EntityIndex,
    int Serial,
    string ClassName,
    int ThrowerSlot,
    string ThrowerName,
    int Team,
    int CreatedTick,
    int RemovedTick,
    int RoundNumber,
    bool Removed,
    IReadOnlyDictionary<string, object?> Values);
