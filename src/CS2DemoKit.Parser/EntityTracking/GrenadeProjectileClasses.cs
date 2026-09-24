namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     The server classes of the five thrown-grenade projectiles, spelled once. A projectile entity
///     exists from the frame the grenade leaves the hand until it is removed after detonation (a
///     smoke stays while its cloud lives), so these are the classes to track for a grenade's flight.
///     <para>
///         The held weapons (<c>CHEGrenade</c>, <c>CSmokeGrenade</c> and so on) are different
///         classes and are not listed. Incendiary grenades arrive as <see cref="Molotov" />; the
///         wire does not reliably say which of the two was thrown.
///     </para>
/// </summary>
public static class GrenadeProjectileClasses
{
    /// <summary>A smoke grenade in flight, and its cloud once it pops.</summary>
    public const string Smoke = "CSmokeGrenadeProjectile";

    /// <summary>A molotov or incendiary grenade in flight.</summary>
    public const string Molotov = "CMolotovProjectile";

    /// <summary>A high-explosive grenade in flight.</summary>
    public const string HEGrenade = "CHEGrenadeProjectile";

    /// <summary>A flashbang in flight.</summary>
    public const string Flashbang = "CFlashbangProjectile";

    /// <summary>A decoy in flight, and while it plays its sound.</summary>
    public const string Decoy = "CDecoyProjectile";

    /// <summary>The five classes, in the order declared above.</summary>
    public static IReadOnlyList<string> All { get; } = [Smoke, Molotov, HEGrenade, Flashbang, Decoy];

    /// <summary>
    ///     Whether <paramref name="className" /> is one of the five projectile classes. An ordinal
    ///     comparison with no allocation, cheap enough for a per-entity filter.
    /// </summary>
    public static bool Contains(string? className) => className switch
    {
        Smoke or Molotov or HEGrenade or Flashbang or Decoy => true,
        _ => false
    };
}
