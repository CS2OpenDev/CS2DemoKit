#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Nodes;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Edges;

/// <summary>
///     Enrichment edge that fires on every <see cref="PlayerHurtEvent" /> and populates
///     shared transient nodes: team classification bools, victim's pre-hit health, and
///     overkill-capped damage amount. Also updates the victim's health in the
///     <see cref="PlayerContextIndex" /> for subsequent events.
///     <para>
///         <b>Entity-state path:</b> when an <see cref="EntityChangeScanner" /> and
///         a <see cref="IPerPlayerEntityValueProvider" /> for pawn health are supplied, the
///         pre-hit HP is read from the scanner's pre-frame snapshot rather than the
///         event-tracked cache. The snapshot reflects PREVIOUS-frame entity state, so it
///         captures non-damage HP changes (medshots, custom-server respawn HP, etc.) that
///         the event-tracked path misses. Falls back to the event-tracked path when either
///         piece is null OR when the snapshot returns no value (pawn absent / first frame) OR
///         when the victim was already hurt earlier in the SAME frame — the snapshot is
///         frame-start state, so within a GOTV multi-hit burst only the event cache carries the
///         true pre-hit health (capping a burst-ending kill at frame-start HP overcounted
///         <c>enemy_damage</c> by +2..+66 per player — KNOWN-AND-SUSPECTED-ISSUES.md).
///     </para>
///     <para>
///         <b>Attacker-weapon enrichment:</b> when an
///         <see cref="ActiveWeaponProvider" /> is supplied (via the matching ctor parameter), the
///         attacker's pre-frame active weapon ClassName (e.g. <c>"CWeaponAK47"</c>) is exposed via
///         the <c>enrich.hurt.attacker_active_weapon</c> transient. This is the weapon held at the
///         start of the frame containing the damage event, not after — entity updates and game events
///         arrive concurrently in the same packet, so the post-frame value can already reflect a
///         swap or drop. Falls back to an empty string when unavailable.
///     </para>
/// </summary>
public sealed class HurtTeamEnrichmentEdge(
    StateNode source,
    PlayerContextIndex playerContext,
    TransientBoolNode wasEnemyDamage,
    TransientBoolNode wasTeamDamage,
    TransientBoolNode wasSelfDamage,
    TransientValueNode<int> victimHealthBefore,
    TransientValueNode<int> cappedDamage,
    TransientValueNode<string> attackerActiveWeapon,
    EntityChangeScanner? scanner = null,
    IPerPlayerEntityValueProvider? pawnHealthProvider = null,
    IPerPlayerEntityValueProvider? activeWeaponProvider = null) : StateEdge(source)
{
    // EnemyDmg-overcount guard: FrameNumber of the last player_hurt processed per victim slot.
    // GOTV coalesces several server ticks into one frame, so a burst that kills a player arrives
    // as MULTIPLE player_hurt events in ONE frame while the pre-frame entity snapshot still holds
    // the FRAME-START health (before the whole burst). For every hit after the first in a frame,
    // the event-tracked cache is exact — it holds the server-reported post-hit health of the
    // previous same-frame hit — so the entity override must stand down or the overkill cap uses a
    // pre-burst HP and overcounts (measured +2..+66 per player vs the Leetify-verified goldens).
    private readonly Dictionary<int, int> _lastVictimHurtFrame = [];

    /// <inheritdoc />
    public override IReadOnlyList<StateNode>? AdditionalWrittenNodes =>
        [wasTeamDamage, wasSelfDamage, victimHealthBefore, cappedDamage, attackerActiveWeapon];

    /// <inheritdoc />
    public override EdgeEffect? DeclaredEffect => EdgeEffect.Activate;

    /// <inheritdoc />
    public override Type MessageType => typeof(PlayerHurtEvent);

    /// <inheritdoc />
    public override StateNode? WrittenNode => wasEnemyDamage;

    /// <inheritdoc />
    public override bool TryApply(EvaluationContext context)
    {
        if (context.Message is not GameEventMessage gem)
        {
            return false;
        }

        if (gem.DecodedEvent.Payload is not PlayerHurtEvent hurt)
        {
            return false;
        }

        int victimSlot = hurt.UserId;
        int attackerSlot = hurt.Attacker;

        // Read victim's HP before this hit. Preferred path: pre-frame entity snapshot
        // (captures non-damage HP changes the event cache misses). Fallback:
        // event-tracked cache in PlayerContextIndex. Take entity value only when
        // strictly positive — null/zero means pawn not yet tracked OR dead, neither
        // of which is a valid "pre-hit" HP for capping.
        //
        // The entity snapshot is FRAME-START health. Once the victim has already been hurt
        // in THIS frame (a GOTV same-frame burst), the snapshot no longer reflects the pre-hit
        // state — the event cache does, exactly (it stores the server-reported post-hit health of
        // the previous same-frame hit). The override therefore only engages for the victim's
        // first hurt of the frame; this is what keeps the overkill cap at the Leetify-verified
        // May-golden values.
        int frameNumber = context.Frame.FrameNumber;
        bool victimAlreadyHurtThisFrame =
            _lastVictimHurtFrame.TryGetValue(victimSlot, out int lastHurtFrame)
            && lastHurtFrame == frameNumber;

        int preHitHp = playerContext.GetHealth(victimSlot);
        if (!victimAlreadyHurtThisFrame && scanner is not null && pawnHealthProvider is not null)
        {
            object? entityHp = scanner.GetPreFrameValue(pawnHealthProvider, victimSlot);
            if (entityHp is int hp and > 0)
            {
                preHitHp = hp;
            }
        }

        victimHealthBefore.SetValue(preHitHp);

        // Compute capped damage (can't deal more damage than victim has HP)
        int capped = hurt.Health > 0 ? hurt.DmgHealth : Math.Min(hurt.DmgHealth, preHitHp);
        cappedDamage.SetValue(capped);

        // Read attacker's active weapon from pre-frame entity snapshot. The
        // event itself carries a weapon string (`hurt.Weapon`) but that path is the
        // server-reported damage source, which can lag or mismatch the actively-held
        // weapon (e.g. utility damage credited to the throwing weapon's class). When
        // the snapshot disagrees, downstream consumers can compare both.
        if (scanner is not null && activeWeaponProvider is not null && attackerSlot >= 0)
        {
            object? weapon = scanner.GetPreFrameValue(activeWeaponProvider, attackerSlot);
            if (weapon is string { Length: > 0 } s)
            {
                attackerActiveWeapon.SetValue(s);
            }
        }

        // Update victim's health post-hit
        playerContext.SetHealth(victimSlot, hurt.Health > 0 ? hurt.Health : 100);
        _lastVictimHurtFrame[victimSlot] = frameNumber;

        // Classify team relationship
        if (attackerSlot == victimSlot)
        {
            wasSelfDamage.Activate();
            return true;
        }

        int attackerTeam = GetTeam(attackerSlot);
        int victimTeam = GetTeam(victimSlot);

        if (attackerTeam > 1 && attackerTeam == victimTeam)
        {
            wasTeamDamage.Activate();
        }
        else
        {
            wasEnemyDamage.Activate();
        }

        return true;
    }

    private int GetTeam(int slot) =>
        playerContext.TryGet(slot, out PlayerContextIndex.PlayerContext? ctx) ? ctx!.Team : 0;
}
