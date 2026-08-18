#region

using System.Runtime.CompilerServices;
using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.SdkAbstractions;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The one production wiring point between an <see cref="EntityTracker" /> and the
///     SDK-emitted wrapper set (<c>CS2OpenDev.Sdk.Entities</c>): a canonical
///     <see cref="TrackerEntityWorld" /> per tracker, with the package's own
///     <see cref="EntityWrapperRegistry" /> factories registered so cross-entity handle
///     companions (<c>ActiveWeapon</c>, <c>PlayerPawn</c>, …) and
///     <see cref="EntityTracker.ResolveHandle{T}" /> dispatch to SDK wrappers.
///     <para>
///         One world per tracker matters twice over: <see cref="TrackerEntityWorld" /> caches
///         its ordinal-translation tables per (engine class, shape) — a fresh world per read
///         would rebuild them — and registering the factories REPLACES any per-class factory
///         the tracker held before, so it must happen once, not per call site.
///     </para>
/// </summary>
public static class SdkEntityWorlds
{
    private static readonly ConditionalWeakTable<EntityTracker, TrackerEntityWorld> Worlds = new();

    private static readonly Dictionary<string, EntityClassBinding> BindingsByEngineClass =
        EntityWrapperRegistry.Bindings.ToDictionary(b => b.EngineClass, StringComparer.Ordinal);

    // One wrapper per EntityState instance. A wrapper is a stateless view (reader over the live
    // state + world), so re-binding one per read only churned allocation — ~540k reader+wrapper
    // pairs per demo eval before this cache. Safe across incarnation reuse: EntitySet.GetOrCreate
    // reuses a state instance only for the SAME engine class (binding unchanged; Clear() keeps the
    // bound Shape), and a class change or slot recreation makes a fresh instance → cache miss.
    // Weak-keyed, so detached FreezeCopy states and dead trackers don't pin wrappers.
    private static readonly ConditionalWeakTable<EntityState, EntityWrapper> WrapperCache = new();

    /// <summary>
    ///     The canonical world for <paramref name="tracker" />, created (and its factories
    ///     registered) on first use. Safe to call repeatedly; the table is weak-keyed so a
    ///     collected tracker takes its world with it.
    /// </summary>
    public static TrackerEntityWorld For(EntityTracker tracker)
        => Worlds.GetValue(tracker, Create);

    /// <summary>
    ///     Binds an SDK wrapper of type <typeparamref name="T" /> over one live entity's
    ///     state — the cutover replacement for <c>new CSPlayerPawn(state, tracker)</c> on the
    ///     retired local wrappers. Returns null when the entity's class has no emitted
    ///     binding, or when the registry's concrete wrapper for that class is not a
    ///     <typeparamref name="T" />.
    /// </summary>
    public static T? Wrap<T>(EntityTracker tracker, EntityState state) where T : EntityWrapper
    {
        if (WrapperCache.TryGetValue(state, out EntityWrapper? cached))
        {
            // Same class → same concrete wrapper type, so a wrong-T cached wrapper answers
            // exactly like a wrong-T fresh Create would: null.
            return cached as T;
        }

        if (!BindingsByEngineClass.TryGetValue(state.ClassName, out EntityClassBinding? binding))
        {
            return null;
        }

        TrackerEntityWorld world = For(tracker);
        EntityWrapper? wrapper =
            EntityWrapperRegistry.Create(state.ClassName, world.CreateReader(binding, state), world);
        if (wrapper is not null)
        {
            WrapperCache.AddOrUpdate(state, wrapper);
        }

        return wrapper as T;
    }

    private static TrackerEntityWorld Create(EntityTracker tracker)
    {
        TrackerEntityWorld world = new(tracker);
        foreach (EntityClassBinding binding in EntityWrapperRegistry.Bindings)
        {
            string engineClass = binding.EngineClass;
            // The two abstract bases (CBaseCSGrenade, CCSWeaponBaseShotgun) have bindings but
            // no Create case — and never appear as a live entity's exact class, so their
            // factory can never be invoked. If one ever fires, that is a discovery worth a
            // loud throw, not a silent local-wrapper fallback.
            world.RegisterWrapper(binding, (r, w) => EntityWrapperRegistry.Create(engineClass, r, w)
                ?? throw new InvalidOperationException(
                    $"EntityWrapperRegistry has no Create case for live entity class '{engineClass}'."));
        }

        return world;
    }
}
