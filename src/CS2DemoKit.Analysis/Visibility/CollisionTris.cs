#region

using System.Text;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Reader for the baker's <c>collision.tris</c> world-collision blob (written by the AssetBaker's
///     <c>CollisionMesh</c>). Format: <c>[uint32 "CTRI"][int32 version][int32 triCount]</c> then
///     <c>triCount × 9 × float32</c> (v0.xyz, v1.xyz, v2.xyz), all little-endian, world space. Pure I/O —
///     no VRF, no geometry logic. The returned float[] is the exact layout the BVH and raycaster consume.
/// </summary>
public static class CollisionTris
{
    /// <summary>"CTRI" as a little-endian uint32.</summary>
    public const uint Magic = 0x49525443;

    /// <summary>Loads a <c>collision.tris</c> file. Throws on a bad magic or truncated body.</summary>
    public static Data Load(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        return Load(fs);
    }

    /// <summary>Loads from an open stream (leaves it open).</summary>
    public static Data Load(Stream stream)
    {
        using BinaryReader r = new(stream, Encoding.UTF8, true);
        uint magic = r.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException($"collision.tris: bad magic 0x{magic:X8} (expected 0x{Magic:X8})");
        }

        _ = r.ReadInt32(); // format version (only v1 today)
        int count = r.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException($"collision.tris: negative triangle count {count}");
        }

        float[] v = new float[(long)count * 9 is var n && n <= int.MaxValue ? (int)n : throw new InvalidDataException("collision.tris: too many triangles")];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = r.ReadSingle();
        }

        return new Data(v, count);
    }

    public readonly record struct Data(float[] Vertices, int TriangleCount);
}
