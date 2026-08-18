#region

using System.Numerics;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser;
using CS2DemoKit.Parser.EntityTracking;
using CS2DemoKit.TestSupport;
using TUnit.Core.Exceptions;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Gates the visibility asset-resolution layer that moved into the package (fixing three
///     long-standing frictions: "no packageable asset-resolution layer", "name-only bundle selection, no
///     version keying", "<c>Analyze</c> uncancellable"):
///     <list type="bullet">
///         <item><see cref="CollisionAssetLocator" />'s resolution order and its never-throw contract;</item>
///         <item><see cref="MapAssetBundleReader" />'s round-trip over a synthetic <c>bundle.json</c>,
///         including the collision-mesh-absent shape;</item>
///         <item>bake identity reaching <see cref="VisibilityAnalyzer.Report" />;</item>
///         <item>cancellation of a running <see cref="VisibilityAnalyzer.Analyze" /> replay.</item>
///     </list>
///     The locator tests drive the env-var branch (the walk-up branch depends on
///     <see cref="AppContext.BaseDirectory" />, so only its miss behaviour is assertable here) and
///     always use a random map name, so a real bake in the checkout can never satisfy them by accident.
/// </summary>
[NotInParallel]
[Category("Unit")]
public class VisibilityAssetLayerTests
{
    /// <summary>A map name no bake on disk can possibly match, so a "found" result is always ours.</summary>
    private static string UniqueMapName() => "de_test_" + Guid.NewGuid().ToString("N");

    // ── CollisionAssetLocator (VIS-3) ─────────────────────────────────────────

    /// <summary>
    ///     Env var, flat layout: <c>&lt;dir&gt;/&lt;map&gt;.tris</c> — the shape a service pointing at a
    ///     downloaded asset pack uses.
    /// </summary>
    [Test]
    public async Task Locator_ResolvesFlatFile_UnderEnvVar()
    {
        string map = UniqueMapName();
        using TempDir root = new();
        string expected = Path.Combine(root.Path, map + ".tris");
        File.WriteAllText(expected, "x");

        using EnvVarScope _ = new(CollisionAssetLocator.EnvVar, root.Path);
        await Assert.That(CollisionAssetLocator.FindCollisionTris(map)).IsEqualTo(expected);
    }

    /// <summary>Env var, bundle layout: <c>&lt;dir&gt;/&lt;map&gt;/collision.tris</c> works unchanged.</summary>
    [Test]
    public async Task Locator_ResolvesBundleLayout_UnderEnvVar()
    {
        string map = UniqueMapName();
        using TempDir root = new();
        string mapDir = Directory.CreateDirectory(Path.Combine(root.Path, map)).FullName;
        string expected = Path.Combine(mapDir, "collision.tris");
        File.WriteAllText(expected, "x");

        using EnvVarScope _ = new(CollisionAssetLocator.EnvVar, root.Path);
        await Assert.That(CollisionAssetLocator.FindCollisionTris(map)).IsEqualTo(expected);
    }

    /// <summary>Documented order: the flat file is probed before the bundle layout, first hit wins.</summary>
    [Test]
    public async Task Locator_PrefersFlatFile_OverBundleLayout()
    {
        string map = UniqueMapName();
        using TempDir root = new();
        string flat = Path.Combine(root.Path, map + ".tris");
        File.WriteAllText(flat, "x");
        string mapDir = Directory.CreateDirectory(Path.Combine(root.Path, map)).FullName;
        File.WriteAllText(Path.Combine(mapDir, "collision.tris"), "x");

        using EnvVarScope _ = new(CollisionAssetLocator.EnvVar, root.Path);
        await Assert.That(CollisionAssetLocator.FindCollisionTris(map)).IsEqualTo(flat);
    }

    /// <summary>
    ///     Every miss is null, never an exception: an unbaked map, a blank name, and a name that is not a
    ///     legal path component all degrade the same way (callers hide the compute action instead of
    ///     crashing).
    /// </summary>
    [Test]
    public async Task Locator_ReturnsNull_OnEveryMiss()
    {
        using TempDir root = new();
        using EnvVarScope _ = new(CollisionAssetLocator.EnvVar, root.Path);

        await Assert.That(CollisionAssetLocator.FindCollisionTris(UniqueMapName())).IsNull();
        await Assert.That(CollisionAssetLocator.FindCollisionTris(null)).IsNull();
        await Assert.That(CollisionAssetLocator.FindCollisionTris("")).IsNull();
        await Assert.That(CollisionAssetLocator.FindCollisionTris("   ")).IsNull();
        await Assert.That(CollisionAssetLocator.FindCollisionTris("../../\0hostile")).IsNull();
    }

    /// <summary>An env var pointing at a directory that does not exist falls through to the walk-up, not a throw.</summary>
    [Test]
    public async Task Locator_ToleratesMissingEnvDirectory()
    {
        using EnvVarScope _ = new(CollisionAssetLocator.EnvVar,
            Path.Combine(Path.GetTempPath(), "cs2demokit-absent-" + Guid.NewGuid().ToString("N")));

        await Assert.That(CollisionAssetLocator.FindCollisionTris(UniqueMapName())).IsNull();
    }

    // ── MapAssetBundleReader (VIS-3 / VIS-4) ──────────────────────────────────

    /// <summary>
    ///     Round-trips the baker's on-disk shape: camelCase property names, a collision-mesh reference,
    ///     two radar layers. Every field the DTOs declare must survive the read — this is the contract a
    ///     package consumer parses bundles against.
    /// </summary>
    [Test]
    public async Task Reader_RoundTripsBundle_WithCollisionMesh()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, MapAssetBundleReader.BundleFileName), BundleJson(
            """
            "collisionMesh": {
              "file": "collision.tris", "triangleCount": 12345,
              "minX": -3000.0, "minY": -2000.0, "minZ": -500.0,
              "maxX": 3000.0, "maxY": 2000.0, "maxZ": 900.0
            }
            """));

        MapAssetBundle? bundle = MapAssetBundleReader.TryRead(dir.Path);

        await Assert.That(bundle).IsNotNull();
        await Assert.That(bundle!.SchemaVersion).IsEqualTo(1);
        await Assert.That(bundle.MapName).IsEqualTo("de_synthetic");
        await Assert.That(bundle.MapVersion).IsEqualTo("a1b2c3d4");
        await Assert.That(bundle.BakerVersion).IsEqualTo("baker-9.9");
        await Assert.That(bundle.Transform.Scale).IsEqualTo(4.5);
        await Assert.That(bundle.Transform.ImageSize).IsEqualTo(1024);
        await Assert.That(bundle.Bounds.MinX).IsEqualTo(-2400.0);
        await Assert.That(bundle.Bounds.MaxY).IsEqualTo(3100.0);
        await Assert.That(bundle.Floors.Count).IsEqualTo(2);
        await Assert.That(bundle.Floors[1].MaxZ).IsEqualTo(640.0);
        await Assert.That(bundle.RadarLayers.Count).IsEqualTo(2);
        await Assert.That(bundle.RadarLayers[1].Image).IsEqualTo("radar_lower.png");
        await Assert.That(bundle.RadarImages.Count).IsEqualTo(2);
        await Assert.That(bundle.CollisionMesh).IsNotNull();
        await Assert.That(bundle.CollisionMesh!.File).IsEqualTo("collision.tris");
        await Assert.That(bundle.CollisionMesh.TriangleCount).IsEqualTo(12345);
        await Assert.That(bundle.CollisionMesh.MaxZ).IsEqualTo(900.0);
    }

    /// <summary>
    ///     The collision-mesh reference is optional (4 Active Duty maps have no bake today), so a bundle
    ///     without it must read cleanly rather than failing the whole manifest.
    /// </summary>
    [Test]
    public async Task Reader_RoundTripsBundle_WithoutCollisionMesh()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, MapAssetBundleReader.BundleFileName), BundleJson(null));

        MapAssetBundle? bundle = MapAssetBundleReader.TryRead(dir.Path);

        await Assert.That(bundle).IsNotNull();
        await Assert.That(bundle!.CollisionMesh).IsNull();
        await Assert.That(bundle.MapName).IsEqualTo("de_synthetic");
        await Assert.That(bundle.Floors.Count).IsEqualTo(2); // the rest of the manifest is intact
    }

    /// <summary>Identity is the map/bake version pair a report carries — VIS-4's whole point.</summary>
    [Test]
    public async Task Reader_ExposesBakeIdentity()
    {
        using TempDir dir = new();
        File.WriteAllText(Path.Combine(dir.Path, MapAssetBundleReader.BundleFileName), BundleJson(null));

        MapBundleIdentity? identity = MapAssetBundleReader.TryReadIdentity(dir.Path);

        await Assert.That(identity).IsEqualTo(new MapBundleIdentity("de_synthetic", "a1b2c3d4", "baker-9.9"));
    }

    /// <summary>
    ///     A manifest that predates version keying has NO identity, and must say so. JSON
    ///     deserialization fills the missing non-nullable strings with null, so without the guard this
    ///     would hand every report a record of nulls — an audit trail that looks present and means
    ///     nothing, which is strictly worse than an absent one (VIS-4).
    /// </summary>
    [Test]
    public async Task Reader_ReportsNoIdentity_WhenManifestPredatesVersionKeying()
    {
        using TempDir dir = new();
        string json = BundleJson(null)
            .Replace("\"mapVersion\": \"a1b2c3d4\",", "", StringComparison.Ordinal)
            .Replace("\"bakerVersion\": \"baker-9.9\",", "", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir.Path, MapAssetBundleReader.BundleFileName), json);

        MapAssetBundle? bundle = MapAssetBundleReader.TryRead(dir.Path);

        await Assert.That(bundle).IsNotNull(); // the rest of the manifest still reads
        await Assert.That(bundle!.Floors.Count).IsEqualTo(2);
        await Assert.That(bundle.Identity).IsNull();
        await Assert.That(MapAssetBundleReader.TryReadIdentity(dir.Path)).IsNull();
    }

    /// <summary>
    ///     The one thing synthetic JSON cannot prove: the reader parses what the BAKER actually writes.
    ///     Runs against every real bundle in the checkout — each must read, and each must yield a
    ///     complete identity, or the App threads a meaningless audit trail into every visibility report.
    ///     Skips when the checkout has no baked assets.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task Reader_ParsesEveryRealBundle_InThisCheckout()
    {
        List<string> dirs = FindRealBundleDirectories();
        if (dirs.Count == 0)
        {
            throw new SkipTestException("no baked map assets in this checkout");
        }

        foreach (string dir in dirs)
        {
            MapAssetBundle? bundle = MapAssetBundleReader.TryRead(dir);
            await Assert.That(bundle).IsNotNull();
            await Assert.That(bundle!.Identity).IsNotNull();
            await Assert.That(bundle.RadarImages.Count).IsGreaterThan(0);
            Console.WriteLine($"{Path.GetFileName(dir),-14} {bundle.Identity}  " +
                              $"floors={bundle.Floors.Count} tris={bundle.CollisionMesh?.TriangleCount ?? 0}");
        }
    }

    /// <summary>Absent, blank, empty and malformed all read as null — the host degrades, never throws.</summary>
    [Test]
    public async Task Reader_ReturnsNull_OnEveryFailure()
    {
        using TempDir empty = new();
        using TempDir malformed = new();
        File.WriteAllText(Path.Combine(malformed.Path, MapAssetBundleReader.BundleFileName), "{ not json");

        await Assert.That(MapAssetBundleReader.TryRead(null)).IsNull();
        await Assert.That(MapAssetBundleReader.TryRead("   ")).IsNull();
        await Assert.That(MapAssetBundleReader.TryRead(empty.Path)).IsNull(); // no bundle.json
        await Assert.That(MapAssetBundleReader.TryRead(malformed.Path)).IsNull();
        await Assert.That(MapAssetBundleReader.TryReadIdentity(malformed.Path)).IsNull();
        await Assert.That(MapAssetBundleReader.FindBundleDirectory(UniqueMapName())).IsNull();
        await Assert.That(MapAssetBundleReader.FindBundleDirectory(null)).IsNull();
        await Assert.That(MapAssetBundleReader.TryReadForMap(UniqueMapName())).IsNull();
    }

    // ── Bake identity on the report (VIS-4) ───────────────────────────────────

    /// <summary>Supplied identity travels onto the report, including the degenerate empty-frames path.</summary>
    [Test]
    public async Task Analyze_CarriesBundleIdentity_WhenSupplied()
    {
        MapBundleIdentity identity = new("de_synthetic", "a1b2c3d4", "baker-9.9");
        VisibilityAnalyzer.Options options = new(Bundle: identity);

        VisibilityAnalyzer.Report onFrames =
            VisibilityAnalyzer.Analyze(SyntheticFrames(64), EmptyEngine(), NoPosition, options);
        VisibilityAnalyzer.Report onNoFrames =
            VisibilityAnalyzer.Analyze([], EmptyEngine(), NoPosition, options);

        await Assert.That(onFrames.Bundle).IsEqualTo(identity);
        await Assert.That(onNoFrames.Bundle).IsEqualTo(identity);
    }

    /// <summary>No identity supplied ⇒ null, never a fabricated one. Consumers must handle "unknown bake".</summary>
    [Test]
    public async Task Analyze_LeavesBundleNull_WhenNotSupplied()
    {
        VisibilityAnalyzer.Report report =
            VisibilityAnalyzer.Analyze(SyntheticFrames(64), EmptyEngine(), NoPosition);

        await Assert.That(report.Bundle).IsNull();
    }

    // ── Cancellation (VIS-8) ──────────────────────────────────────────────────

    /// <summary>
    ///     An already-canceled token aborts before any replay work, and an uncanceled one over the same
    ///     frames completes — so the check is inside the replay loop and does not fire spuriously.
    /// </summary>
    [Test]
    public async Task Analyze_ThrowsOnCanceledToken_AndCompletesWithout()
    {
        List<DemoFrame> frames = SyntheticFrames(256);
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            VisibilityAnalyzer.Analyze(frames, EmptyEngine(), NoPosition, null, cts.Token));

        VisibilityAnalyzer.Report ok =
            VisibilityAnalyzer.Analyze(frames, EmptyEngine(), NoPosition, null, CancellationToken.None);
        await Assert.That(ok.SampledTicks).IsEqualTo(0); // no live pawns in synthetic frames
    }

    /// <summary>
    ///     Mid-replay cancellation on a real demo: the injected position resolver cancels once it has been
    ///     called enough times to prove the replay was genuinely under way, and <c>Analyze</c> unwinds
    ///     instead of running to completion. The engine is an EMPTY triangle soup — this gate is about the
    ///     replay loop, not about geometry, so it needs no collision bake.
    /// </summary>
    [Test]
    [Category("Integration")]
    public async Task Analyze_CancelsMidReplay_FromInjectedResolver()
    {
        ParsedDemo demo = DemoTestHelper.GetOrParse(DemoTestHelper.RequireDemo());
        const int cancelAfter = 200;

        using CancellationTokenSource cts = new();
        int calls = 0;
        Vector3? Resolver(EntityState pawn)
        {
            if (Interlocked.Increment(ref calls) >= cancelAfter)
            {
                cts.Cancel();
            }

            return PositionUtil.CellToWorldVector(pawn);
        }

        Assert.Throws<OperationCanceledException>(() =>
            VisibilityAnalyzer.Analyze(demo.Frames, EmptyEngine(), Resolver, null, cts.Token));

        Console.WriteLine($"resolver calls before cancel unwound: {calls:N0}");
        await Assert.That(calls).IsGreaterThanOrEqualTo(cancelAfter);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    ///     Every real baked-bundle directory in the checkout, found by the same walk-up convention
    ///     <see cref="MapAssetBundleReader.FindBundleDirectory" /> uses (<c>assets/</c> first, then the
    ///     gitignored <c>cs2-assets/baked/</c> dev cache). Empty on a checkout without assets.
    /// </summary>
    private static List<string> FindRealBundleDirectories()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (string root in (string[])
                     [
                         Path.Combine(dir.FullName, "assets"),
                         Path.Combine(dir.FullName, "cs2-assets", "baked")
                     ])
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                List<string> found =
                [
                    .. Directory.EnumerateDirectories(root)
                        .Where(d => File.Exists(Path.Combine(d, MapAssetBundleReader.BundleFileName)))
                        .Order(StringComparer.Ordinal)
                ];
                if (found.Count > 0)
                {
                    return found;
                }
            }

            dir = dir.Parent;
        }

        return [];
    }

    /// <summary>Zero triangles: every ray is clear. Enough for the replay-loop gates above.</summary>
    private static VisibilityEngine EmptyEngine() => VisibilityEngine.FromTriangles([], 0);

    private static Vector3? NoPosition(EntityState pawn) => null;

    /// <summary>
    ///     Message-free frames on a 64-tick clock. The replay advances over them and finds no live pawns,
    ///     which is exactly what the identity/cancellation gates need — neither depends on entity content.
    /// </summary>
    private static List<DemoFrame> SyntheticFrames(int count)
    {
        List<DemoFrame> frames = new(count);
        for (int i = 0; i < count; i++)
        {
            frames.Add(new DemoFrame
            {
                Command = "DEM_Packet",
                FrameNumber = i,
                ServerTick = i * 8,
                RawStart = 0,
                RawLength = 1,
                HeaderLength = 1,
                IsCompressed = false,
                MessageList = []
            });
        }

        return frames;
    }

    /// <summary>
    ///     The baker's on-disk shape (camelCase), with <paramref name="collisionMeshMember" /> spliced in
    ///     or omitted. Written as literal JSON rather than serialized, so the test pins the WIRE format a
    ///     package consumer's bundles are in — not just the reader's agreement with itself.
    /// </summary>
    private static string BundleJson(string? collisionMeshMember) =>
        $$"""
          {
            "schemaVersion": 1,
            "mapName": "de_synthetic",
            "mapVersion": "a1b2c3d4",
            "bakerVersion": "baker-9.9",
            "transform": {
              "posX": -2400.0, "posY": 3100.0, "scale": 4.5,
              "rotate": 0.0, "zoom": 1.1, "imageSize": 1024
            },
            "bounds": { "minX": -2400.0, "minY": -1200.0, "maxX": 1800.0, "maxY": 3100.0 },
            "floors": [ { "minZ": -180.0, "maxZ": 120.0 }, { "minZ": 300.0, "maxZ": 640.0 } ],
            "radarLayers": [
              { "minZ": 300.0, "maxZ": 640.0, "image": "radar.png" },
              { "minZ": -180.0, "maxZ": 120.0, "image": "radar_lower.png" }
            ],
            "radarImages": [ "radar.png", "radar_lower.png" ]{{(collisionMeshMember is null
                ? ""
                : ",\n  " + collisionMeshMember)}}
          }
          """;

    /// <summary>A temp directory that deletes itself, so a failing assert cannot leak one.</summary>
    private sealed class TempDir : IDisposable
    {
        public TempDir() => Path = Directory.CreateTempSubdirectory("cs2demokit-assets-").FullName;

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // best-effort cleanup
            }
        }
    }

    /// <summary>Sets an env var for the scope and restores the previous value (null included).</summary>
    private sealed class EnvVarScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public EnvVarScope(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
