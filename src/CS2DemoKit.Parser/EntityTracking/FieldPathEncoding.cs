namespace CS2DemoKit.Parser.EntityTracking;

// Adapted from demofile-net (MIT): https://github.com/saul/demofile-net
// Changes: namespace; FieldPath API uses path[^1]/path[i] indexers (no AddToLast/AddToIndex helpers).

/// <summary>
///     Pre-built Huffman tree for decoding the 39 field-path op codes used in
///     Valve's entity delta encoding. Adapted from demofile-net (MIT).
/// </summary>
internal static class FieldPathEncoding
{
    internal static readonly HuffmanNode<FieldPathEncodingOp> HuffmanRoot;

    static FieldPathEncoding()
    {
        FieldPathEncodingOp[] ops = new FieldPathEncodingOp[]
        {
            new("PlusOne", 36271, (ref b, ref p) => { p[^1] += 1; }), new("PlusTwo", 10334, (ref b, ref p) => { p[^1] += 2; }), new("PlusThree", 1375, (ref b, ref p) => { p[^1] += 3; }), new("PlusFour", 646, (ref b, ref p) => { p[^1] += 4; }), new("PlusN", 4128, (ref b, ref p) => { p[^1] += b.ReadUBitVarFieldPath() + 5; }),
            new("PushOneLeftDeltaZeroRightZero", 35, (ref b, ref p) => { p.Add(0); }), new("PushOneLeftDeltaZeroRightNonZero", 3, (ref b, ref p) => { p.Add(b.ReadUBitVarFieldPath()); }), new("PushOneLeftDeltaOneRightZero", 521, (ref b, ref p) =>
            {
                p[^1] += 1;
                p.Add(0);
            }),
            new("PushOneLeftDeltaOneRightNonZero", 2942, (ref b, ref p) =>
            {
                p[^1] += 1;
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushOneLeftDeltaNRightZero", 560, (ref b, ref p) =>
            {
                p[^1] += b.ReadUBitVarFieldPath();
                p.Add(0);
            }),
            new("PushOneLeftDeltaNRightNonZero", 471, (ref b, ref p) =>
            {
                p[^1] += b.ReadUBitVarFieldPath() + 2;
                p.Add(b.ReadUBitVarFieldPath() + 1);
            }),
            new("PushOneLeftDeltaNRightNonZeroPack6Bits", 10530, (ref b, ref p) =>
            {
                p[^1] += (int)b.ReadUBits(3) + 2;
                p.Add((int)b.ReadUBits(3) + 1);
            }),
            new("PushOneLeftDeltaNRightNonZeroPack8Bits", 251, (ref b, ref p) =>
            {
                p[^1] += (int)b.ReadUBits(4) + 2;
                p.Add((int)b.ReadUBits(4) + 1);
            }),
            new("PushTwoLeftDeltaZero", 0, (ref b, ref p) =>
            {
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushTwoPack5LeftDeltaZero", 0, (ref b, ref p) =>
            {
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
            }),
            new("PushThreeLeftDeltaZero", 0, (ref b, ref p) =>
            {
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushThreePack5LeftDeltaZero", 0, (ref b, ref p) =>
            {
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
            }),
            new("PushTwoLeftDeltaOne", 0, (ref b, ref p) =>
            {
                p[^1] += 1;
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushTwoPack5LeftDeltaOne", 0, (ref b, ref p) =>
            {
                p[^1] += 1;
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
            }),
            new("PushThreeLeftDeltaOne", 0, (ref b, ref p) =>
            {
                p[^1] += 1;
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushThreePack5LeftDeltaOne", 0, (ref b, ref p) =>
            {
                p[^1] += 1;
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
            }),
            new("PushTwoLeftDeltaN", 0, (ref b, ref p) =>
            {
                p[^1] += (int)b.ReadUBitVar() + 2;
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushTwoPack5LeftDeltaN", 0, (ref b, ref p) =>
            {
                p[^1] += (int)b.ReadUBitVar() + 2;
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
            }),
            new("PushThreeLeftDeltaN", 0, (ref b, ref p) =>
            {
                p[^1] += (int)b.ReadUBitVar() + 2;
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
                p.Add(b.ReadUBitVarFieldPath());
            }),
            new("PushThreePack5LeftDeltaN", 0, (ref b, ref p) =>
            {
                p[^1] += (int)b.ReadUBitVar() + 2;
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
                p.Add((int)b.ReadUBits(5));
            }),
            new("PushN", 0, (ref b, ref p) =>
            {
                int count = (int)b.ReadUBitVar();
                p[^1] += (int)b.ReadUBitVar();
                for (int i = 0; i < count; ++i)
                {
                    p.Add(b.ReadUBitVarFieldPath());
                }
            }),
            new("PushNAndNonTopological", 310, (ref b, ref p) =>
            {
                for (int i = 0; i < p.Count; ++i)
                {
                    if (b.ReadOneBit())
                    {
                        p[i] += b.ReadVarInt32() + 1;
                    }
                }

                int count = (int)b.ReadUBitVar();
                for (int i = 0; i < count; ++i)
                {
                    p.Add(b.ReadUBitVarFieldPath());
                }
            }),
            new("PopOnePlusOne", 2, (ref b, ref p) =>
            {
                p.Pop(1);
                if (p.Count > 0)
                {
                    p[^1] += 1;
                }
            }),
            new("PopOnePlusN", 0, (ref b, ref p) =>
            {
                p.Pop(1);
                if (p.Count > 0)
                {
                    p[^1] += b.ReadUBitVarFieldPath() + 1;
                }
            }),
            new("PopAllButOnePlusOne", 1837, (ref b, ref p) =>
            {
                p.Pop(p.Count - 1);
                p[0] += 1;
            }),
            new("PopAllButOnePlusN", 149, (ref b, ref p) =>
            {
                p.Pop(p.Count - 1);
                p[0] += b.ReadUBitVarFieldPath() + 1;
            }),
            new("PopAllButOnePlusNPack3Bits", 300, (ref b, ref p) =>
            {
                p.Pop(p.Count - 1);
                p[0] += (int)b.ReadUBits(3) + 1;
            }),
            new("PopAllButOnePlusNPack6Bits", 634, (ref b, ref p) =>
            {
                p.Pop(p.Count - 1);
                p[0] += (int)b.ReadUBits(6) + 1;
            }),
            new("PopNPlusOne", 0, (ref b, ref p) =>
            {
                p.Pop(b.ReadUBitVarFieldPath());
                if (p.Count > 0)
                {
                    p[^1] += 1;
                }
            }),
            new("PopNPlusN", 0, (ref b, ref p) =>
            {
                p.Pop(b.ReadUBitVarFieldPath());
                if (p.Count > 0)
                {
                    p[^1] += b.ReadVarInt32();
                }
            }),
            new("PopNAndNonTopographical", 1, (ref b, ref p) =>
            {
                p.Pop(b.ReadUBitVarFieldPath());
                for (int i = 0; i < p.Count; ++i)
                {
                    if (b.ReadOneBit())
                    {
                        p[i] += b.ReadVarInt32();
                    }
                }
            }),
            new("NonTopoComplex", 76, (ref b, ref p) =>
            {
                for (int i = 0; i < p.Count; ++i)
                {
                    if (b.ReadOneBit())
                    {
                        p[i] += b.ReadVarInt32();
                    }
                }
            }),
            new("NonTopoPenultimatePlusOne", 271, (ref b, ref p) =>
            {
                if (p.Count >= 2)
                {
                    p[^2] += 1;
                }
            }),
            new("NonTopoComplexPack4Bits", 99, (ref b, ref p) =>
            {
                for (int i = 0; i < p.Count; ++i)
                {
                    if (b.ReadOneBit())
                    {
                        p[i] += (int)b.ReadUBits(4) - 7;
                    }
                }
            }),
            new("FieldPathEncodeFinish", 25474, null) // null Reader = stop
        };

        HuffmanRoot = HuffmanNode<FieldPathEncodingOp>.Build(
            ops.Select(op => new KeyValuePair<FieldPathEncodingOp, int>(op, op.Frequency)));
    }

    /// <summary>Reads the next field-path op by traversing the Huffman tree one bit at a time.</summary>
    public static FieldPathEncodingOp ReadOp(ref BitBuffer buffer)
    {
        HuffmanNode<FieldPathEncodingOp> node = HuffmanRoot;
        for (;;)
        {
            HuffmanNode<FieldPathEncodingOp> next = (buffer.ReadOneBit() ? node.Right : node.Left)
                                                    ?? throw new InvalidDataException("Invalid field path Huffman code in entity_data.");

            if (next.Symbol is { } op)
            {
                return op;
            }

            node = next;
        }
    }
}
