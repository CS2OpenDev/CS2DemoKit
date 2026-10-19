#region

using System.Globalization;
using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Analysis.Observation;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;

#endregion

namespace CS2DemoKit.Analysis.Nodes;

/// <summary>
///     One projectile instance's row as the current frame holds it: the scanner's live row while
///     the projectile flies, its last-seen row on the frame it is removed, and after that the row
///     last read. Shared by every pull node the instance materialized.
/// </summary>
internal sealed class ProjectileInstanceState(EntityChangeScanner? scanner, ProjectileRow created, int firstSeenTick)
{
    private ProjectileRow _last = created;
    private int _lastMoved = firstSeenTick;

    /// <summary>The frame-clock tick the projectile first appeared on.</summary>
    public int FirstSeenTick { get; } = firstSeenTick;

    /// <summary>
    ///     The frame-clock tick the projectile last moved on, as of the last <see cref="Current" />
    ///     read: the spawn until the lifecycle tracks movement and sees a change.
    /// </summary>
    public int LastMovedTick => _lastMoved;

    /// <summary>The instance's entity index.</summary>
    public int EntityIndex => _last.EntityIndex;

    /// <summary>The instance's entity serial.</summary>
    public int Serial => _last.Serial;

    /// <summary>The row as of the current frame.</summary>
    public ProjectileRow Current
    {
        get
        {
            if (scanner is null)
            {
                return _last;
            }

            // Both spans are short (the live grenades of one frame) and a read is rare: an edge
            // fire or the instance's finalization, never per frame.
            if (!Find(scanner.Projectiles.Live, scanner.Projectiles.LiveLastMoved))
            {
                Find(scanner.Projectiles.Removed, scanner.Projectiles.RemovedLastMoved);
            }

            return _last;
        }
    }

    // The moved span is aligned with the rows when the lifecycle tracks movement, else empty.
    private bool Find(ReadOnlySpan<ProjectileRow> rows, ReadOnlySpan<int> moved)
    {
        for (int i = 0; i < rows.Length; i++)
        {
            if (rows[i].EntityIndex == _last.EntityIndex && rows[i].Serial == _last.Serial)
            {
                _last = rows[i];
                if (moved.Length == rows.Length)
                {
                    _lastMoved = moved[i];
                }

                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     A <c>projectile.&lt;member&gt;</c> read of one instance: always active, recomputed from the
///     instance's <see cref="ProjectileInstanceState.Current" /> row on every read, and excluded
///     from snapshots like every pull node. The expression compiler reads <see cref="Value" />
///     reflectively; a <c>keep: last</c> capture with no <c>on:</c> is this node itself.
/// </summary>
/// <typeparam name="T"><c>double</c> for a number, <c>string</c> for a name, <c>bool</c> for a flag.</typeparam>
internal sealed class ProjectileValuePullNode<T>(
    string name, string? subtitle, ProjectileInstanceState state, Func<ProjectileRow, T> read)
    : StateNode, ISnapshotExcludedNode
{
    /// <inheritdoc />
    public override bool IsActive => true;

    /// <inheritdoc />
    public override string Name { get; } = name;

    /// <inheritdoc />
    public override string? Subtitle { get; } = subtitle;

    /// <summary>The member's value off the instance's current row.</summary>
    public T Value => read(state.Current);

    /// <inheritdoc />
    public override string? GetDisplayValue() =>
        Value switch
        {
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            var other => other?.ToString()
        };

    /// <inheritdoc />
    public override float? GetNumericValue() =>
        Value switch
        {
            double d => (float)d,
            bool b => b ? 1f : 0f,
            _ => null
        };
}

/// <summary>Builds the pull node behind each <see cref="CatalogScopeAdapter.ProjectileMembers" /> entry.</summary>
internal static class ProjectileMemberNodes
{
    // Built once over the catalog's member list; read on every materialization.
    private static readonly Dictionary<string, (string Key, string Name)> Names =
        CatalogScopeAdapter.ProjectileMembers.ToDictionary(
            m => m.Member, m => ($"entity.projectile.{m.Member}", $"__projectile_{m.Member}"), StringComparer.Ordinal);

    /// <summary>The lookup key a <c>projectile.&lt;member&gt;</c> read lowers to in a condition or value.</summary>
    public static string Key(string member) => Names.TryGetValue(member, out (string Key, string Name) entry)
        ? entry.Key
        : $"entity.projectile.{member}";

    /// <summary>The name of the pull node behind <paramref name="member" />, the same for every instance.</summary>
    public static string NodeName(string member) => Names.TryGetValue(member, out (string Key, string Name) entry)
        ? entry.Name
        : $"__projectile_{member}";

    /// <summary>The pull node for <paramref name="member" /> over <paramref name="state" />.</summary>
    public static StateNode Create(string member, string name, string? subtitle, ProjectileInstanceState state) =>
        member switch
        {
            "class" => new ProjectileValuePullNode<string>(name, subtitle, state, r => ProjectileGrenadeName.Of(r.ClassName)),
            "class_name" => new ProjectileValuePullNode<string>(name, subtitle, state, r => r.ClassName),
            "thrower" => Number(name, subtitle, state, r => r.ThrowerSlot),
            "team" => Number(name, subtitle, state, r => r.Team),
            "entity_index" => Number(name, subtitle, state, r => r.EntityIndex),
            "serial" => Number(name, subtitle, state, r => r.Serial),
            "pos_x" => Number(name, subtitle, state, r => r.Position?.X ?? double.NaN),
            "pos_y" => Number(name, subtitle, state, r => r.Position?.Y ?? double.NaN),
            "pos_z" => Number(name, subtitle, state, r => r.Position?.Z ?? double.NaN),
            "initial_x" => Number(name, subtitle, state, r => r.InitialPosition?.X ?? double.NaN),
            "initial_y" => Number(name, subtitle, state, r => r.InitialPosition?.Y ?? double.NaN),
            "initial_z" => Number(name, subtitle, state, r => r.InitialPosition?.Z ?? double.NaN),
            "velocity_x" => Number(name, subtitle, state, r => r.InitialVelocity?.X ?? double.NaN),
            "velocity_y" => Number(name, subtitle, state, r => r.InitialVelocity?.Y ?? double.NaN),
            "velocity_z" => Number(name, subtitle, state, r => r.InitialVelocity?.Z ?? double.NaN),
            "bounces" => Number(name, subtitle, state, r => r.Bounces),
            "first_seen_tick" => Number(name, subtitle, state, _ => state.FirstSeenTick),
            "last_moved_tick" => Number(name, subtitle, state, _ => state.LastMovedTick),
            "smoke_effect_tick" => Number(name, subtitle, state, r => r.SmokeEffectTickBegin),
            "smoke_detonation_x" => Number(name, subtitle, state, r => r.SmokeDetonationPosition?.X ?? double.NaN),
            "smoke_detonation_y" => Number(name, subtitle, state, r => r.SmokeDetonationPosition?.Y ?? double.NaN),
            "smoke_detonation_z" => Number(name, subtitle, state, r => r.SmokeDetonationPosition?.Z ?? double.NaN),
            "is_incendiary" => new ProjectileValuePullNode<bool>(name, subtitle, state, r => r.IsIncendiary ?? false),
            "explode_effect_tick" => Number(name, subtitle, state, r => r.ExplodeEffectTickBegin ?? 0),
            _ => throw new InvalidOperationException($"'projectile.{member}' is not a projectile member")
        };

    private static ProjectileValuePullNode<double> Number(string name, string? subtitle, ProjectileInstanceState state,
        Func<ProjectileRow, double> read) => new(name, subtitle, state, read);
}
