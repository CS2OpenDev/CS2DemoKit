#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests.EntityTracking;

/// <summary>
///     The per-update field-path cap (#56). Under the old 2,048 cap a smoke's instancebaseline
///     (3,214 to 3,482 paths) was cut off silently and its values decoded from bits that were really
///     path ops. The cap is now 16,384 and going over it throws, so these check both sides of it on
///     synthetic streams built from the real Huffman tree.
/// </summary>
public class FieldPathCapTests
{
    /// <summary>
    ///     <see cref="BitBuffer" /> reads zeros past its end, so an all-zero stream is what a
    ///     misaligned read runs into. It must decode as a non-finish op, or the cap would never be
    ///     reached on it.
    /// </summary>
    [Test]
    public async Task ZeroCode_IsANonFinishOp()
    {
        HuffmanNode<FieldPathEncodingOp> node = FieldPathEncoding.HuffmanRoot;
        while (node.Symbol is null)
        {
            node = node.Left!;
        }

        await Assert.That(node.Symbol.Name).IsEqualTo("PlusOne");
        await Assert.That(node.Symbol.Reader).IsNotNull();
    }

    [Test]
    public async Task ZeroStream_PastTheCap_Throws()
    {
        byte[] zeros = new byte[EntityTracker.MaxFieldPaths / 8 + 64];
        List<FieldPath> paths = [];

        InvalidDataException? thrown = null;
        try
        {
            Collect(zeros, paths);
        }
        catch (InvalidDataException ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("CTestEntity");
        await Assert.That(thrown.Message).Contains(EntityTracker.MaxFieldPaths.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(paths.Count).IsEqualTo(EntityTracker.MaxFieldPaths);
    }

    /// <summary>More paths than the old cap and at least the largest real smoke baseline.</summary>
    [Test]
    public async Task FinishCode_BelowTheCap_ReturnsEveryPath()
    {
        const int count = 3_500;
        byte[] stream = BuildStream(count);
        List<FieldPath> paths = [];

        Collect(stream, paths);

        await Assert.That(paths.Count).IsEqualTo(count);
        FieldPath last = paths[^1];
        int[] lastPath = last.AsSpan().ToArray();
        await Assert.That(lastPath).IsEquivalentTo(new[] { count - 1 });
    }

    [Test]
    public async Task FinishCode_ExactlyAtTheCap_ReturnsEveryPath()
    {
        byte[] stream = BuildStream(EntityTracker.MaxFieldPaths);
        List<FieldPath> paths = [];

        Collect(stream, paths);

        await Assert.That(paths.Count).IsEqualTo(EntityTracker.MaxFieldPaths);
    }

    private static void Collect(byte[] bytes, List<FieldPath> paths)
    {
        BitBuffer buf = new(bytes);
        EntityTracker.CollectFieldPaths(ref buf, paths, EntityTracker.MaxFieldPaths, "CTestEntity", null);
    }

    /// <summary><paramref name="plusOnes" /> PlusOne ops followed by the finish op, LSB-first.</summary>
    private static byte[] BuildStream(int plusOnes)
    {
        List<bool> plusOne = CodeFor("PlusOne");
        List<bool> finish = CodeFor("FieldPathEncodeFinish");
        List<bool> bits = new(plusOnes * plusOne.Count + finish.Count);
        for (int i = 0; i < plusOnes; i++)
        {
            bits.AddRange(plusOne);
        }

        bits.AddRange(finish);

        byte[] bytes = new byte[bits.Count / 8 + 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                bytes[i / 8] |= (byte)(1 << (i % 8));
            }
        }

        return bytes;
    }

    private static List<bool> CodeFor(string opName)
    {
        List<bool> code = [];
        if (!Find(FieldPathEncoding.HuffmanRoot, opName, code))
        {
            throw new InvalidOperationException($"No Huffman code for {opName}");
        }

        return code;
    }

    private static bool Find(HuffmanNode<FieldPathEncodingOp>? node, string opName, List<bool> code)
    {
        if (node is null)
        {
            return false;
        }

        if (node.Symbol is { } op)
        {
            return op.Name == opName;
        }

        code.Add(false);
        if (Find(node.Left, opName, code))
        {
            return true;
        }

        code[^1] = true;
        if (Find(node.Right, opName, code))
        {
            return true;
        }

        code.RemoveAt(code.Count - 1);
        return false;
    }
}
