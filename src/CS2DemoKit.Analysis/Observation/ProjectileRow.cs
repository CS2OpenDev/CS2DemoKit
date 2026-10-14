#region

using System.Numerics;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Observation;

/// <summary>
///     One grenade projectile as the per-frame entity digest read it: the state at the end of the
///     frame, for one of the <see cref="GrenadeProjectileClasses" />. Mirrors
///     <see cref="ProjectileSample" /> without the walk's bookkeeping (frame index, tick, the
///     created and removed flags), which the consumer that diffs frames supplies instead.
/// </summary>
/// <param name="EntityIndex">The projectile's entity index. Reused by later entities once it is removed.</param>
/// <param name="Serial">The entity serial. <c>(EntityIndex, Serial)</c> identifies one projectile for its whole life.</param>
/// <param name="ClassName">One of <see cref="GrenadeProjectileClasses" />.</param>
/// <param name="ThrowerSlot">
///     The thrower's player slot, 0-63, through <see cref="PawnLookup.ResolveThrowerSlot" />, or -1
///     when the chain does not resolve on this frame. A row read straight off the digest carries
///     the frame's own resolution; the lifecycle that diffs frames holds the first resolved slot
///     for the rest of the projectile's life, as <see cref="ProjectileSample.ThrowerSlot" /> does.
/// </param>
/// <param name="Team">The projectile's <c>m_iTeamNum</c> (2 = T, 3 = CT), or -1 when unseen.</param>
/// <param name="Position">
///     World position from the cell and offset leaves, or null when a leaf is unseen or a cell
///     index is 0 (outside every map, so garbage rather than a position).
/// </param>
/// <param name="InitialPosition"><c>m_vInitialPosition</c>, where the grenade left the hand. Null when unseen.</param>
/// <param name="InitialVelocity"><c>m_vInitialVelocity</c> at release. Null when unseen.</param>
/// <param name="Bounces"><c>m_nBounces</c>, or 0 when unseen.</param>
/// <param name="SmokeEffectTickBegin">
///     A smoke's <c>m_nSmokeEffectTickBegin</c>: 0 while it flies, the server tick its cloud began
///     once it pops. 0 for every other class.
/// </param>
/// <param name="SmokeDetonationPosition">A smoke's <c>m_vSmokeDetonationPos</c> once it pops. Null for every other class, and before.</param>
/// <param name="IsIncendiary">
///     A molotov projectile's <c>m_bIsIncGrenade</c>: true for an incendiary grenade, false for a
///     molotov. Null for every other class, and when the field is unseen.
/// </param>
public readonly record struct ProjectileRow(
    int EntityIndex,
    int Serial,
    string ClassName,
    int ThrowerSlot,
    int Team,
    Vector3? Position,
    Vector3? InitialPosition,
    Vector3? InitialVelocity,
    int Bounces,
    int SmokeEffectTickBegin,
    Vector3? SmokeDetonationPosition,
    bool? IsIncendiary);
