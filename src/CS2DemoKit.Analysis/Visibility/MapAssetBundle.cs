#region

using System.Security;
using System.Text.Json;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

// The read side of the asset baker's bundle.json. Pure data + a BCL-only reader: consumers of this
// package get the manifest (map/baker version, world bounds, floor bands, collision-mesh reference)
// without taking an imaging dependency. Materializing the radar images the manifest names is
// deliberately a host concern and lives with the host's imaging stack.

/// <summary>The world→radar affine placement (raw overview-txt values).</summary>
/// <param name="PosX">World X of the radar image's top-left corner.</param>
/// <param name="PosY">World Y of the radar image's top-left corner.</param>
/// <param name="Scale">World units per radar pixel.</param>
/// <param name="Rotate">Radar rotation in degrees.</param>
/// <param name="Zoom">Overview zoom factor.</param>
/// <param name="ImageSize">Radar image edge length in pixels (square).</param>
public sealed record RadarTransform(
    double PosX,
    double PosY,
    double Scale,
    double Rotate,
    double Zoom,
    int ImageSize);

/// <summary>The map's world-space playable rectangle.</summary>
public sealed record WorldBoundsDto(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>One nav-derived walkable floor band (world Z).</summary>
public sealed record FloorBandDto(double MinZ, double MaxZ);

/// <summary>Which radar image applies over a Z band (overview-txt verticalsections).</summary>
public sealed record RadarLayerDto(double MinZ, double MaxZ, string Image);

/// <summary>
///     Reference to the baked world-collision blob — the 3D line-of-sight geometry
///     <see cref="VisibilityEngine.Load" /> consumes. Absent on maps without a bake.
///     <see cref="File" /> is a name relative to the bundle directory; the bounds are the collision
///     AABB in world units.
/// </summary>
public sealed record CollisionMeshDto(
    string File,
    int TriangleCount,
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ);

/// <summary>
///     Which bake produced the geometry a run was computed against — the audit trail that tells a
///     stale result from a fresh one after a CS2 map update. Bundles are selected by map NAME alone,
///     so without this a result carries no evidence of which geometry it actually raycast.
/// </summary>
/// <param name="MapName">The map the bundle was baked for (e.g. <c>de_dust2</c>).</param>
/// <param name="MapVersion">
///     Content-derived version of the source assets (a CRC over the radar/nav/overview bytes) — it
///     changes when Valve ships a new version of the map.
/// </param>
/// <param name="BakerVersion">
///     Version of the baker that produced the bundle — it changes when extraction behavior changes
///     even though the source assets did not.
/// </param>
public sealed record MapBundleIdentity(string MapName, string MapVersion, string BakerVersion);

/// <summary>
///     Deserialized <c>bundle.json</c> — the baked, ready-to-use map-asset manifest. Additive by
///     design: an older consumer ignores fields it does not know, and <see cref="CollisionMesh" /> is
///     absent on maps with no collision bake.
/// </summary>
public sealed record MapAssetBundle(
    int SchemaVersion,
    string MapName,
    string MapVersion,
    string BakerVersion,
    RadarTransform Transform,
    WorldBoundsDto Bounds,
    IReadOnlyList<FloorBandDto> Floors,
    IReadOnlyList<RadarLayerDto> RadarLayers,
    IReadOnlyList<string> RadarImages,
    CollisionMeshDto? CollisionMesh = null)
{
    /// <summary>
    ///     This bundle's bake identity, to attach to any result computed from its geometry (see
    ///     <see cref="VisibilityAnalyzer.Options.Bundle" />). <c>null</c> when the manifest does not
    ///     carry all three fields — a bundle baked before version keying existed has NO identity, and
    ///     saying so is the point; handing back a record of nulls would put a meaningless audit trail
    ///     on every report instead of an honestly absent one. (JSON deserialization fills a missing
    ///     non-nullable string with null, so this is the guard that turns that into the documented
    ///     "unknown bake" state.)
    /// </summary>
    public MapBundleIdentity? Identity =>
        string.IsNullOrWhiteSpace(MapName) || string.IsNullOrWhiteSpace(MapVersion) ||
        string.IsNullOrWhiteSpace(BakerVersion)
            ? null
            : new MapBundleIdentity(MapName, MapVersion, BakerVersion);
}

/// <summary>
///     Locates and reads a map's baked <c>bundle.json</c>. BCL-only and <b>never throws</b>: a
///     missing, unreadable or malformed bundle reads as <c>null</c> so a host degrades instead of
///     failing (the asset pack is optional — see <see cref="CollisionAssetLocator" /> for why baked
///     geometry does not ship inside the package).
/// </summary>
public static class MapAssetBundleReader
{
    /// <summary>The manifest's file name inside a bundle directory.</summary>
    public const string BundleFileName = "bundle.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    ///     Finds the directory holding a map's baked bundle, or null when the map has none. Walks up
    ///     from <see cref="AppContext.BaseDirectory" /> looking for <c>assets/&lt;map&gt;/</c> (a
    ///     deployed asset pack) then <c>cs2-assets/baked/&lt;map&gt;/</c> (a dev cache) at each
    ///     level — the same convention <see cref="CollisionAssetLocator" /> uses, so the collision
    ///     blob and the manifest describing it resolve to the same place.
    /// </summary>
    public static string? FindBundleDirectory(string? mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName))
        {
            return null;
        }

        try
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string shipped = Path.Combine(dir.FullName, "assets", mapName);
                if (Directory.Exists(shipped))
                {
                    return shipped;
                }

                string devCache = Path.Combine(dir.FullName, "cs2-assets", "baked", mapName);
                if (Directory.Exists(devCache))
                {
                    return devCache;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException)
        {
            // A hostile map name / unreadable directory is a "no bundle", not a crash.
        }

        return null;
    }

    /// <summary>
    ///     Reads <c>bundle.json</c> from an explicit bundle directory. Null when the directory is
    ///     null/blank, holds no manifest, or the manifest is unreadable or malformed.
    /// </summary>
    public static MapAssetBundle? TryRead(string? bundleDirectory)
    {
        if (string.IsNullOrWhiteSpace(bundleDirectory))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(bundleDirectory, BundleFileName);
            return !File.Exists(path)
                ? null
                : JsonSerializer.Deserialize<MapAssetBundle>(File.ReadAllText(path), _jsonOptions);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or SecurityException
                                       or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Locates and reads a map's bundle in one call. Null when the map has no readable bundle.</summary>
    public static MapAssetBundle? TryReadForMap(string? mapName) => TryRead(FindBundleDirectory(mapName));

    /// <summary>
    ///     Reads just the bake identity from a bundle directory — the cheap call for attaching an
    ///     audit trail to a computed result. Null when there is no readable manifest there, or when
    ///     the manifest predates version keying (see <see cref="MapAssetBundle.Identity" />). Both are
    ///     legitimate states — a flat <c>&lt;dir&gt;/&lt;map&gt;.tris</c> collision override has no
    ///     sibling manifest at all — so callers treat the identity as optional throughout.
    /// </summary>
    public static MapBundleIdentity? TryReadIdentity(string? bundleDirectory) =>
        TryRead(bundleDirectory)?.Identity;
}
