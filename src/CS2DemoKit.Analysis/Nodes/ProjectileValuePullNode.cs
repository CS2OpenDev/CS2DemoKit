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
internal sealed class ProjectileInstanceState(EntityChangeScanner? scanner, ProjectileRow created)
{
    private ProjectileRow _last = created;

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
            foreach (ProjectileRow row in scanner.Projectiles.Live)
            {
                if (row.EntityIndex == _last.EntityIndex && row.Serial == _last.Serial)
                {
                    _last = row;
                    return row;
                }
            }

            foreach (ProjectileRow row in scanner.Projectiles.Removed)
            {
                if (row.EntityIndex == _last.EntityIndex && row.Serial == _last.Serial)
                {
                    _last = row;
                    return row;
                }
            }

            return _last;
        }
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
    private static readonly Dictionary<string, (string Key, string Name)> Names = new(StringComparer.Ordinal);

    /// <summary>The lookup key a <c>projectile.&lt;member&gt;</c> read lowers to in a condition or value.</summary>
    public static string Key(string member) => Entry(member).Key;

    /// <summary>The name of the pull node behind <paramref name="member" />, the same for every instance.</summary>
    public static string NodeName(string member) => Entry(member).Name;

    private static (string Key, string Name) Entry(string member)
    {
        lock (Names)
        {
            if (!Names.TryGetValue(member, out (string Key, string Name) entry))
            {
                Names[member] = entry = ($"entity.projectile.{member}", $"__projectile_{member}");
            }

            return entry;
        }
    }

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
            "smoke_effect_tick" => Number(name, subtitle, state, r => r.SmokeEffectTickBegin),
            "is_incendiary" => new ProjectileValuePullNode<bool>(name, subtitle, state, r => r.IsIncendiary ?? false),
            _ => throw new InvalidOperationException($"'projectile.{member}' is not a projectile member")
        };

    private static ProjectileValuePullNode<double> Number(string name, string? subtitle, ProjectileInstanceState state,
        Func<ProjectileRow, double> read) => new(name, subtitle, state, read);
}
