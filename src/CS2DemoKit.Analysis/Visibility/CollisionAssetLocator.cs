#region

using System.Security;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Resolves the per-map baked collision blob (<c>collision.tris</c> — the 3D line-of-sight
///     geometry consumed by <see cref="VisibilityEngine.Load" />). Purely name→path lookup, with no
///     per-map behavior branches (map-independence principle), so <see cref="VisibilityAnalyzer" />
///     itself stays path-free and a host that acquires geometry some other way can ignore this type
///     entirely.
///     <para>
///         <b>Baked geometry never ships inside a NuGet package</b> — it is Valve-derived and large.
///         This locator is the supported way to point a consumer at an out-of-band asset pack.
///     </para>
///     <para>
///         <b>Resolution order</b> (first hit wins; env-first, bundles later):
///         <list type="number">
///             <item>
///                 <c>CS2DEMOKIT_COLLISION_DIR</c> env var: <c>&lt;dir&gt;/&lt;map&gt;.tris</c>,
///                 then <c>&lt;dir&gt;/&lt;map&gt;/collision.tris</c> (a bundle-layout dir works as-is).
///                 This is the knob a headless service sets; the name is a legacy identifier kept
///                 stable for existing deployments.
///             </item>
///             <item>
///                 Walk up from <see cref="AppContext.BaseDirectory" /> to the first
///                 <c>assets/&lt;map&gt;/collision.tris</c> (an asset pack unpacked next to the
///                 binary), then <c>cs2-assets/baked/&lt;map&gt;/collision.tris</c> (the repo's dev
///                 cache — the same convention as <see cref="MapAssetBundleReader" /> and the
///                 visibility oracle tests).
///             </item>
///         </list>
///         Missing/unresolvable asset → <c>null</c>, never a throw — callers degrade (hide the
///         compute action, emit one info diagnostic).
///     </para>
/// </summary>
public static class CollisionAssetLocator
{
    /// <summary>The env var naming a directory that holds per-map collision bakes.</summary>
    public const string EnvVar = "CS2DEMOKIT_COLLISION_DIR";

    // The name this override shipped under before the library moved out of the DemoViewer.NET repo.
    private const string LegacyEnvVar = "DEMOVIEWER_COLLISION_DIR";

    /// <summary>
    ///     Returns the absolute path of the map's <c>collision.tris</c> bake, or null when the map
    ///     has no resolvable bake (or <paramref name="mapName" /> is null/empty). Never throws.
    /// </summary>
    public static string? FindCollisionTris(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        try
        {
            // 1. Explicit env-var directory (developer override / packaged bundles).
            string? envDir = Environment.GetEnvironmentVariable(EnvVar) ?? Environment.GetEnvironmentVariable(LegacyEnvVar);
            if (!string.IsNullOrWhiteSpace(envDir))
            {
                string flat = Path.Combine(envDir, mapName + ".tris");
                if (File.Exists(flat))
                {
                    return flat;
                }

                string nested = Path.Combine(envDir, mapName, "collision.tris");
                if (File.Exists(nested))
                {
                    return nested;
                }
            }

            // 2. Walk up from the running assembly. Primary is `assets/<map>/` — where a deployed
            //    asset pack lands next to the binary; `cs2-assets/baked/<map>/` is the dev-cache
            //    fallback, which is what a checkout of this repo has.
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string shipped = Path.Combine(dir.FullName, "assets", mapName, "collision.tris");
                if (File.Exists(shipped))
                {
                    return shipped;
                }

                string devCache = Path.Combine(dir.FullName, "cs2-assets", "baked", mapName, "collision.tris");
                if (File.Exists(devCache))
                {
                    return devCache;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException)
        {
            // A hostile map name / unreadable directory is a "no bake", not a crash.
        }

        return null;
    }
}
