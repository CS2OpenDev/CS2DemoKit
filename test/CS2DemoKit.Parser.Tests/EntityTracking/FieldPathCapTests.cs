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

    /// <summary>
    ///     A run of PlusOne ops past the cap. The run is all zero bits, which is what a misaligned read
    ///     sees, and it ends in a finish op so that a tracker with the cap removed returns normally
    ///     and fails this test instead of looping until the process runs out of memory.
    /// </summary>
    [Test]
    public async Task ZeroRun_PastTheCap_Throws()
    {
        await Assert.That(CodeFor("PlusOne").TrueForAll(bit => !bit)).IsTrue();
        byte[] stream = BuildStream(EntityTracker.MaxFieldPaths + 64);
        List<FieldPath> paths = [];

        InvalidDataException? thrown = Capture(stream, paths, "Baseline");

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("'CTestEntity' (Baseline) carried more than");
        await Assert.That(thrown.Message).Contains(EntityTracker.MaxFieldPaths.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Assert.That(paths.Count).IsEqualTo(EntityTracker.MaxFieldPaths);
    }

    /// <summary>The message leaves out the parenthesised update kind when the caller has none.</summary>
    [Test]
    public async Task PastTheCap_WithoutAnUpdateKind_HasNoEmptyParentheses()
    {
        byte[] stream = BuildStream(EntityTracker.MaxFieldPaths + 1);
        List<FieldPath> paths = [];

        InvalidDataException? thrown = Capture(stream, paths, "");

        await Assert.That(thrown).IsNotNull();
        await Assert.That(thrown!.Message).Contains("'CTestEntity' carried more than");
        await Assert.That(thrown.Message).DoesNotContain("()");
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

    private static void Collect(byte[] bytes, List<FieldPath> paths, string updateKind = "Delta")
    {
        BitBuffer buf = new(bytes);
        EntityTracker.CollectFieldPaths(ref buf, paths, EntityTracker.MaxFieldPaths, "CTestEntity", updateKind, null);
    }

    private static InvalidDataException? Capture(byte[] bytes, List<FieldPath> paths, string updateKind)
    {
        try
        {
            Collect(bytes, paths, updateKind);
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex;
        }
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
