#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Analysis.Events;

// The short names the projectile views' `class` facet matches on. Must stay in step with
// GrenadeProjectileClasses: a class missing here reads as its server class name.
internal static class ProjectileGrenadeName
{
    public static string Of(string className) => className switch
    {
        GrenadeProjectileClasses.Smoke => "smoke",
        GrenadeProjectileClasses.Molotov => "molotov",
        GrenadeProjectileClasses.HEGrenade => "he",
        GrenadeProjectileClasses.Flashbang => "flash",
        GrenadeProjectileClasses.Decoy => "decoy",
        _ => className
    };
}
