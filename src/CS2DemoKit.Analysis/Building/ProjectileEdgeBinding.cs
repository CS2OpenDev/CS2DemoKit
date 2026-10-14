#region

using CS2DemoKit.Analysis.Events;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     Binds a <c>for: each_projectile</c> instance's edge to its own projectile events. A closure,
///     not a compiled condition: hundreds of instances a demo each lower the same edge, and the
///     only thing that differs between them is the pair below.
/// </summary>
internal static class ProjectileEdgeBinding
{
    /// <summary>
    ///     The condition for the instance <paramref name="entityIndex" />:<paramref name="serial" />:
    ///     the event names that projectile, and <paramref name="inner" /> holds when there is one.
    /// </summary>
    public static Func<GameEvent, bool> Bind(int entityIndex, int serial, Func<GameEvent, bool>? inner) =>
        inner is null
            ? e => Names(e, entityIndex, serial)
            : e => Names(e, entityIndex, serial) && inner(e);

    private static bool Names(GameEvent e, int entityIndex, int serial) =>
        e switch
        {
            ProjectileCreatedEvent created => created.EntityIndex == entityIndex && created.Serial == serial,
            ProjectileRemovedEvent removed => removed.EntityIndex == entityIndex && removed.Serial == serial,
            _ => false
        };
}
