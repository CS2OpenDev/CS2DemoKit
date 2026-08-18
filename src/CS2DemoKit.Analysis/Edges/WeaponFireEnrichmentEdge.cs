#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Classifies a weapon name as bullet-fire (rifles/pistols/SMGs/LMGs) or not.
///     Shotguns (xm1014/nova/mag7/sawedoff) fire pellet volleys and are excluded
///     from this category because the "shots fired" / "shots hit foe" stats
///     only count single-bullet weapons. Knives, grenades, c4, taser, fists, etc.
///     are also excluded.
/// </summary>
public static class WeaponClassification
{
    /// <summary>
    ///     Returns <c>true</c> when the given weapon name (with or without <c>weapon_</c> prefix)
    ///     refers to a single-bullet weapon — i.e. a rifle, pistol, SMG, or LMG. Shotguns, knives,
    ///     grenades, c4, taser, fists, and unknown items return <c>false</c>.
    /// </summary>
    public static bool IsBulletWeapon(string? weapon)
    {
        if (string.IsNullOrEmpty(weapon))
        {
            return false;
        }

        // Strip optional "weapon_" prefix — weapon_fire events use it, player_hurt
        // events generally do not. Knife variants share the "knife" stem in both
        // forms (weapon_knife_t, weapon_knife_butterfly, etc.).
        ReadOnlySpan<char> name = weapon.StartsWith("weapon_", StringComparison.Ordinal)
            ? weapon.AsSpan("weapon_".Length)
            : weapon.AsSpan();

        if (name.StartsWith("knife", StringComparison.Ordinal))
        {
            return false;
        }

        // Bayonet is a knife class but its name doesn't start with "knife".
        if (name.SequenceEqual("bayonet"))
        {
            return false;
        }

        return name switch
        {
            "hegrenade" or "flashbang" or "smokegrenade" or "molotov" or "incgrenade"
                or "decoy" or "c4" or "taser" or "zeus_x27" or "fists" or "breachcharge"
                or "bumpmine" or "tablet" or "melee" or "inferno" or "world"
                or "snowball" or "healthshot" or "diversion" or "tagrenade"
                or "shield" or "axe" or "hammer" or "spanner" or "frag_grenade"
                // Shotguns: pellet-spread weapons excluded from the shots stats.
                or "xm1014" or "nova" or "mag7" or "sawedoff" => false,
            _ => true
        };
    }
}

/// <summary>
///     Sets <c>enrich.weapon_fire.is_bullet</c> on weapon_fire events for bullet
///     weapons only — see <see cref="WeaponClassification.IsBulletWeapon" />.
/// </summary>
public sealed class WeaponFireEnrichmentEdge(StateNode source, TransientBoolNode isBullet) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => typeof(WeaponFireEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => isBullet;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        if (payload is not WeaponFireEvent fire)
        {
            return false;
        }

        if (WeaponClassification.IsBulletWeapon(fire.Weapon))
        {
            isBullet.Activate();
            return true;
        }

        return false;
    }
}

/// <summary>
///     Sets <c>enrich.hurt.is_bullet</c> on player_hurt events caused by bullet
///     weapons only, with shotgun multi-pellet deduplication. A shotgun blast
///     emits one player_hurt event per pellet that connects; the hit stats count
///     that as a single hit, so we suppress duplicates with the same
///     attacker/victim at the same tick.
/// </summary>
public sealed class HurtBulletEnrichmentEdge(StateNode source, PlayerContextIndex ctx, TransientBoolNode isBullet) : StateEdge(source)
{
    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerHurtEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => isBullet;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context) => false;

    /// <inheritdoc />
    public override bool TryApplyDirect(object payload, EvaluationContext context)
    {
        if (payload is not PlayerHurtEvent hurt)
        {
            return false;
        }

        if (!WeaponClassification.IsBulletWeapon(hurt.Weapon))
        {
            return false;
        }

        if (hurt.Attacker < 0 || hurt.Attacker == hurt.UserId)
        {
            return false;
        }

        if (ctx.TryGet(hurt.Attacker, out PlayerContextIndex.PlayerContext? attackerCtx) && attackerCtx is not null)
        {
            // Suppress shotgun multi-pellet duplicates: same attacker hitting
            // the same victim at the same tick is one logical "shot hit".
            if (attackerCtx.LastHurtTick == context.Fire!.GameTick &&
                attackerCtx.LastHurtVictim == hurt.UserId)
            {
                return false;
            }

            attackerCtx.LastHurtTick = context.Fire!.GameTick;
            attackerCtx.LastHurtVictim = hurt.UserId;
        }

        isBullet.Activate();
        return true;
    }
}
