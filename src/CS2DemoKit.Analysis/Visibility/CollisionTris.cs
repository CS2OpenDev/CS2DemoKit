#region

using System.Buffers.Binary;
using System.Runtime.InteropServices;

#endregion

namespace CS2DemoKit.Analysis.Visibility;

/// <summary>
///     Reader for the baker's <c>collision.tris</c> world-collision blob (written by the AssetBaker's
///     <c>CollisionMesh</c>). Format: <c>[uint32 "CTRI"][int32 version][int32 triCount]</c> then
///     <c>triCount × 9 × float32</c> (v0.xyz, v1.xyz, v2.xyz), all little-endian, world space. Pure I/O —
///     no VRF, no geometry logic. The returned float[] is the exact layout the BVH and raycaster consume.
///     <para>
///         The body is read straight into the float array's bytes, one <c>ReadExactly</c> for the
///         whole blob, rather than one virtual <c>ReadSingle</c> per value (a large bake is nine
///         million of them). On a little-endian host the reinterpretation is exactly what
///         <c>BinaryReader.ReadSingle</c> does; on a big-endian one the bytes are swapped in place,
///         so the result is the same either way. <c>CollisionTrisTests</c> holds the two readers to
///         bit equality.
///     </para>
/// </summary>
public static class CollisionTris
{
    /// <summary>"CTRI" as a little-endian uint32.</summary>
    public const uint Magic = 0x49525443;

    private const int HeaderBytes = 12;

    /// <summary>Loads a <c>collision.tris</c> file. Throws on a bad magic or truncated body.</summary>
    public static Data Load(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        return Load(fs);
    }

    /// <summary>Loads from an open stream (leaves it open). Throws <see cref="EndOfStreamException" /> on a truncated body.</summary>
    public static Data Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[HeaderBytes];
        stream.ReadExactly(header);
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (magic != Magic)
        {
            throw new InvalidDataException($"collision.tris: bad magic 0x{magic:X8} (expected 0x{Magic:X8})");
        }

        // header[4..8] is the format version (only v1 today).
        int count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (count < 0)
        {
            throw new InvalidDataException($"collision.tris: negative triangle count {count}");
        }

        long floats = (long)count * 9;
        if (floats > int.MaxValue)
        {
            throw new InvalidDataException("collision.tris: too many triangles");
        }

        float[] v = new float[(int)floats];
        Span<byte> body = MemoryMarshal.AsBytes(v.AsSpan());
        stream.ReadExactly(body);
        if (!BitConverter.IsLittleEndian)
        {
            Span<int> bits = MemoryMarshal.Cast<byte, int>(body);
            for (int i = 0; i < bits.Length; i++)
            {
                bits[i] = BinaryPrimitives.ReverseEndianness(bits[i]);
            }
        }

        return new Data(v, count);
    }

    public readonly record struct Data(float[] Vertices, int TriangleCount);
}
