#region

using System.Buffers.Binary;
using System.Text;
using CS2DemoKit.Analysis.Visibility;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     Holds <see cref="CollisionTris.Load(string)" /> to a byte-for-byte reference: the
///     <see cref="BinaryReader" /> loop the reader was first written as, kept here so a faster
///     reader is measured against the original rather than against itself.
///     <para>
///         Bit patterns, not values, are compared: a reader that reinterprets bytes has to carry
///         NaN payloads, negative zero and subnormals through unchanged, and a value comparison
///         would call two different NaNs equal.
///     </para>
/// </summary>
[Category("Unit")]
public class CollisionTrisTests
{
    [Test]
    public async Task Load_MatchesTheBinaryReaderReference_OnASyntheticFile()
    {
        using TempFile file = new(SyntheticTris(20260910, 4096));

        CollisionTris.Data data = CollisionTris.Load(file.Path);
        (float[] expected, int expectedCount) = ReferenceLoad(file.Path);

        await Assert.That(data.TriangleCount).IsEqualTo(expectedCount);
        await Assert.That(data.Vertices.Length).IsEqualTo(expected.Length);
        await Assert.That(FirstBitwiseMismatch(data.Vertices, expected)).IsEqualTo(-1)
            .Because("every float must reach the caller with the exact bits that were on disk");
    }

    [Test]
    [Category("RealAsset")]
    public async Task Load_MatchesTheBinaryReaderReference_OnTheNukeBake()
    {
        string path = VisibilityReplay.RequireBakePath(VisibilityReplay.SampleDemoMap);

        CollisionTris.Data data = CollisionTris.Load(path);
        (float[] expected, int expectedCount) = ReferenceLoad(path);

        await Assert.That(data.TriangleCount).IsEqualTo(expectedCount);
        await Assert.That(expectedCount).IsGreaterThan(100_000).Because("a real bake has six-figure triangle counts");
        await Assert.That(FirstBitwiseMismatch(data.Vertices, expected)).IsEqualTo(-1);
    }

    [Test]
    public async Task Load_LeavesTheStreamOpen_AndPositionedAfterTheBody()
    {
        byte[] bytes = SyntheticTris(7, 3);
        using MemoryStream stream = new(bytes);

        CollisionTris.Data data = CollisionTris.Load(stream);

        await Assert.That(data.TriangleCount).IsEqualTo(3);
        await Assert.That(stream.CanRead).IsTrue().Because("the stream overload leaves the stream open");
        await Assert.That(stream.Position).IsEqualTo(bytes.Length);
    }

    [Test]
    public void Load_RejectsABadMagic()
    {
        byte[] bytes = SyntheticTris(1, 2);
        bytes[0] ^= 0xFF;
        using MemoryStream stream = new(bytes);

        Assert.Throws<InvalidDataException>(() => CollisionTris.Load(stream));
    }

    [Test]
    public void Load_RejectsANegativeCount()
    {
        byte[] bytes = SyntheticTris(1, 2);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), -1);
        using MemoryStream stream = new(bytes);

        Assert.Throws<InvalidDataException>(() => CollisionTris.Load(stream));
    }

    [Test]
    public void Load_ThrowsOnATruncatedBody()
    {
        byte[] bytes = SyntheticTris(1, 8);
        using MemoryStream stream = new(bytes, 0, bytes.Length - 5);

        Assert.Throws<EndOfStreamException>(() => CollisionTris.Load(stream));
    }

    /// <summary>The original reader, verbatim: header via BinaryReader, then one ReadSingle per float.</summary>
    private static (float[] Vertices, int TriangleCount) ReferenceLoad(string path)
    {
        using FileStream fs = new(path, FileMode.Open, FileAccess.Read);
        using BinaryReader r = new(fs, Encoding.UTF8, true);
        uint magic = r.ReadUInt32();
        if (magic != CollisionTris.Magic)
        {
            throw new InvalidDataException("bad magic");
        }

        _ = r.ReadInt32();
        int count = r.ReadInt32();
        float[] v = new float[count * 9];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = r.ReadSingle();
        }

        return (v, count);
    }

    private static int FirstBitwiseMismatch(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (BitConverter.SingleToInt32Bits(a[i]) != BitConverter.SingleToInt32Bits(b[i]))
            {
                return i;
            }
        }

        return a.Length == b.Length ? -1 : n;
    }

    // A valid header followed by random 32-bit patterns: ordinary values, but also NaNs with
    // payloads, negative zero, infinities and subnormals, which are exactly the patterns a
    // reinterpreting reader could normalise away.
    private static byte[] SyntheticTris(int seed, int triangles)
    {
        Random rng = new(seed);
        byte[] bytes = new byte[12 + triangles * 9 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0, 4), CollisionTris.Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8, 4), triangles);
        for (int i = 0; i < triangles * 9; i++)
        {
            uint bits = (i % 7) switch
            {
                0 => 0x7FC00000u | (uint)rng.Next(1, 0x3FFFFF), // NaN with a payload
                1 => 0x80000000u, // negative zero
                2 => (uint)rng.Next(1, 0x7FFFFF), // subnormal
                3 => rng.Next(2) == 0 ? 0x7F800000u : 0xFF800000u, // infinities
                _ => BitConverter.SingleToUInt32Bits((float)(rng.NextDouble() * 8192.0 - 4096.0))
            };
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12 + i * 4, 4), bits);
        }

        return bytes;
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(byte[] bytes)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ctri-" + Guid.NewGuid().ToString("N") + ".tris");
            File.WriteAllBytes(Path, bytes);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                File.Delete(Path);
            }
            catch (IOException)
            {
                // best effort
            }
            catch (UnauthorizedAccessException)
            {
                // Windows reports a file still held open, or one left read-only, here rather than as
                // an IOException; a leaked temp file is not a test failure either way.
            }
        }
    }
}
