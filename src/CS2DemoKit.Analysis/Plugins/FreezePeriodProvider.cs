#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins.Markers;
using CS2DemoKit.Parser.EntityTracking;
using SchemaNames = CS2OpenSchema.SchemaNames;

#endregion

namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     First-consumer <see cref="IEntityValueProvider" /> — reads
///     <c>CCSGameRules.m_bFreezePeriod</c> and exposes it as <c>entity.game.freeze_period</c>.
///     Drives the FreezeTime transition on HLTV demos where the <c>round_prestart</c> event
///     fires 0× and the existing event-based trigger never reaches.
///     <para>
///         CS2 networks the freeze-period flag on <c>CCSGameRulesProxy.m_pGameRules</c> (a
///         pointer to the inner <c>CCSGameRules</c> struct). The wire-decoded field path on the
///         proxy entity is the dotted <c>m_pGameRules.m_bFreezePeriod</c>. There is exactly one
///         <c>CCSGameRulesProxy</c> per match — verified by the Furia parser regression test
///         (<c>EntityTracker_FuriaMirage_NoPhantomGameRulesProxyCreations</c> asserts a count of 1).
///     </para>
///     <para>
///         Only the <see cref="ChangeDirection.RisingOnly" /> transition (false→true) is emitted.
///         The exit path (true→false) remains handled by the existing
///         <c>round_freeze_end → ActiveWithBuy</c> event trigger on both MM and HLTV — avoids
///         the same-tick race between the falling edge and that event.
///     </para>
/// </summary>
public sealed class FreezePeriodProvider : IEntityValueProvider
{
    // Dotted wire path: CCSGameRulesProxy.m_pGameRules.m_bFreezePeriod is delivered as the flat
    // key "m_pGameRules.m_bFreezePeriod" on the proxy entity's Fields dict.
    private static readonly string _dottedFieldPath =
        SchemaNames.CCSGameRulesProxy.GameRules + "." + SchemaNames.CCSGameRules.FreezePeriod;

    // Cache the proxy entity index. Slots can be reused across map restarts; if the cached index
    // ever resolves to an entity with a different ClassName we invalidate and re-scan.
    private int _cachedEntityIndex = -1;

    /// <inheritdoc />
    public string ContextName => "entity.game.freeze_period";

    /// <inheritdoc />
    public object? DefaultValue => false;

    /// <inheritdoc />
    public ChangeDirection EmitOn => ChangeDirection.RisingOnly;

    /// <inheritdoc />
    public string EntityClass => "CCSGameRulesProxy";

    /// <inheritdoc />
    public string FieldName => _dottedFieldPath;

    /// <inheritdoc />
    public Type MarkerType => typeof(CCSGameRulesFreezePeriodMarker);

    /// <inheritdoc />
    public object? Read(EntityStateLayer layer)
    {
        EntityTracker tracker = layer.Tracker;
        EntityState? entity = ResolveEntity(tracker);
        if (entity is null)
        {
            return null;
        }

        // Single-key seen-gated read via the indexer instead of entity.Fields, which rebuilds the
        // ENTIRE per-entity dict projection on every access (~2 GiB / load — the provider-poll alloc).
        // The indexer returns null for an unseen field (the _seen[] bitvector gates every lane and it
        // falls through to the fallback dict), byte-identical to the old Fields.TryGetValue-false path;
        // a received value (incl. 0) flows into the same coercion below.
        object? v = entity[FieldName];

        // CS2 networks "bool" fields as varints. EntityState stores them as int (int lane / typed
        // dict path). Coerce to the declared ValueType; null (field not yet received) stays null.
        return v switch
        {
            bool b => b,
            int i => i != 0,
            uint u => u != 0,
            _ => null
        };
    }

    /// <inheritdoc />
    public Type ValueType => typeof(bool);

    private EntityState? ResolveEntity(EntityTracker tracker)
    {
        if (_cachedEntityIndex >= 0)
        {
            EntityState? cached = tracker.CurrentEntities[_cachedEntityIndex];
            if (cached is not null && cached.ClassName == EntityClass)
            {
                return cached;
            }

            _cachedEntityIndex = -1;
        }

        foreach ((int idx, EntityState ent) in tracker.CurrentEntities.AllIndexed())
        {
            if (ent.ClassName != EntityClass)
            {
                continue;
            }

            _cachedEntityIndex = idx;
            return ent;
        }

        return null;
    }
}
