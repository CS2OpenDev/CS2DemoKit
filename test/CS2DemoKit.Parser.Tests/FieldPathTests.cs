#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     Tests for the <c>FieldPath</c> ref-struct and its companion
///     <c>FieldPathEncoding</c>. <c>FieldPath</c> is the small fixed-capacity
///     descent path that the entity-delta encoding's Huffman-coded ops mutate;
///     its 7-slot capacity is a hardcoded ceiling worth pinning.
///     Tests are unit-level (no demo) and cover
///     the value semantics + edge cases:
///     <list type="bullet">
///         <item>
///             Add up to 7 items, 8th throws (the "FieldPath is full" failure
///             mode that surfaces during bit-misaligned decode runs).
///         </item>
///         <item>Indexer get/set across all 7 slots + range checking.</item>
///         <item>
///             Pop with the silent over-pop clamp (load-bearing: misaligned
///             streams can over-pop, and the clamp keeps the decoder
///             progressing rather than crashing).
///         </item>
///         <item>IReadOnlyList iteration order matches the indexer.</item>
///     </list>
///     Plus a smoke test that <see cref="FieldPathEncoding.ReadOp" /> returns
///     a non-null op for the all-zeroes encoding (Huffman tree successfully
///     decodes the most-common branch).
/// </summary>
[Category("Unit")]
public class FieldPathTests
{
    /// <summary>Add_fills all seven slots_then throws.</summary>
    [Test]
    public async Task Add_FillsAllSevenSlots_ThenThrows()
    {
        FieldPath p = new();
        // Add 7 items — all should succeed.
        for (int i = 0; i < 7; i++)
        {
            p.Add(i * 10);
        }

        await Assert.That(p.Count).IsEqualTo(7);
        for (int i = 0; i < 7; i++)
        {
            await Assert.That(p[i]).IsEqualTo(i * 10);
        }

        // 8th must throw — this is the "FieldPath is full" surface that the
        // entity decoder relies on to detect bit-misalignment.
        bool threw = false;
        try
        {
            p.Add(70);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        await Assert.That(threw).IsTrue();
    }

    /// <summary>Default_is single element minus one.</summary>
    [Test]
    public async Task Default_IsSingleElementMinusOne()
    {
        FieldPath p = FieldPath.Default;
        await Assert.That(p.Count).IsEqualTo(1);
        await Assert.That(p[0]).IsEqualTo(-1);
    }

    // ── FieldPathEncoding integration ─────────────────────────────────────────
    // The op lambdas are private inline closures, but the HuffmanRoot is
    // exposed (internal). Reach in via InternalsVisibleTo (set in ParsedDemo.cs)
    // and verify the Huffman tree decodes valid op codes from a buffer. We
    // don't hand-craft individual op encodings here — the intent
    // for FieldPathEncoding is "lights-on" coverage, not exhaustive.
    /// <summary>Field path encoding_huffman root_decodes valid op.</summary>
    [Test]
    public async Task FieldPathEncoding_HuffmanRoot_DecodesValidOp()
    {
        // Any bit pattern produces SOME op (the Huffman tree spans all
        // sequences); the test asserts that ReadOp returns a non-null op
        // for a simple all-zeros buffer rather than throwing/returning null.
        byte[] data =
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        string opName;
        {
            BitBuffer bb = new(data);
            FieldPathEncodingOp op = FieldPathEncoding.ReadOp(ref bb);
            opName = op.Name;
        }
        await Assert.That(opName).IsNotNull();
        await Assert.That(opName.Length).IsGreaterThan(0);
    }

    /// <summary>Indexer_get out of range_throws.</summary>
    [Test]
    public async Task Indexer_GetOutOfRange_Throws()
    {
        FieldPath p = new();
        p.Add(5);

        bool throwsAtIndex1 = false;
        try
        {
            _ = p[1];
        }
        catch (ArgumentOutOfRangeException)
        {
            throwsAtIndex1 = true;
        }

        await Assert.That(throwsAtIndex1).IsTrue();

        bool throwsAtIndexNeg = false;
        try
        {
            _ = p[-1];
        }
        catch (ArgumentOutOfRangeException)
        {
            throwsAtIndexNeg = true;
        }

        await Assert.That(throwsAtIndexNeg).IsTrue();
    }

    /// <summary>Indexer_set_updates in place.</summary>
    [Test]
    public async Task Indexer_Set_UpdatesInPlace()
    {
        FieldPath p = new();
        p.Add(1);
        p.Add(2);
        p.Add(3);

        p[1] = 99;

        await Assert.That(p[0]).IsEqualTo(1);
        await Assert.That(p[1]).IsEqualTo(99);
        await Assert.That(p[2]).IsEqualTo(3);
    }

    /// <summary>Iteration_matches indexer order.</summary>
    [Test]
    public async Task Iteration_MatchesIndexerOrder()
    {
        FieldPath p = new();
        p.Add(7);
        p.Add(11);
        p.Add(13);

        List<int> collected = new();
        foreach (int v in p)
        {
            collected.Add(v);
        }

        await Assert.That(collected.Count).IsEqualTo(3);
        await Assert.That(collected[0]).IsEqualTo(7);
        await Assert.That(collected[1]).IsEqualTo(11);
        await Assert.That(collected[2]).IsEqualTo(13);
    }

    /// <summary>Last element indexer_accesses and mutates last.</summary>
    [Test]
    public async Task LastElementIndexer_AccessesAndMutatesLast()
    {
        FieldPath p = new();
        p.Add(10);
        p.Add(20);
        p.Add(30);

        // The Huffman ops use `path[^1]` extensively (PlusOne, PlusTwo, etc.).
        // Verify both the read and the increment work.
        int last = p[^1];
        await Assert.That(last).IsEqualTo(30);

        p[^1] += 5;
        await Assert.That(p[^1]).IsEqualTo(35);
        await Assert.That(p[2]).IsEqualTo(35);
        // Earlier elements unchanged.
        await Assert.That(p[0]).IsEqualTo(10);
        await Assert.That(p[1]).IsEqualTo(20);
    }

    /// <summary>Pop_clamps at zero_on overpop.</summary>
    [Test]
    public async Task Pop_ClampsAtZero_OnOverpop()
    {
        // Load-bearing behaviour: misaligned streams produce over-pops, and
        // the decoder relies on Pop clamping (not throwing) to skip the
        // subsequent payload reads rather than crash mid-frame.
        FieldPath p = new();
        p.Add(1);
        p.Add(2);

        p.Pop(99);

        await Assert.That(p.Count).IsEqualTo(0);
    }

    /// <summary>Pop_reduces size.</summary>
    [Test]
    public async Task Pop_ReducesSize()
    {
        FieldPath p = new();
        p.Add(1);
        p.Add(2);
        p.Add(3);
        p.Add(4);

        p.Pop(2);

        await Assert.That(p.Count).IsEqualTo(2);
        await Assert.That(p[0]).IsEqualTo(1);
        await Assert.That(p[1]).IsEqualTo(2);
    }
}
