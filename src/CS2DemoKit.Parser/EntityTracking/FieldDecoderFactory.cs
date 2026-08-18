#region

using System.Buffers.Binary;
using System.Numerics;
using CS2DemoKit.Parser.Entities;

#endregion

namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Factory: given a <see cref="RuntimeField" />, returns a <see cref="FieldDecoder" />
///     that reads the correct number of bits and returns the decoded value.
///     Adapted from demofile-net's FieldDecode.cs (MIT).
/// </summary>
internal static class FieldDecoderFactory
{
    /// <summary>Returns a <see cref="FieldDecoder" /> matching the field's encoding metadata.</summary>
    public static FieldDecoder Create(RuntimeField field)
    {
        FieldEncodingInfo enc = FieldEncodingInfo.From(field);
        // Schema-attribute overrides — see TryCreateFloat for the rationale.
        if (enc.VarEncoder is null && field.Name is "m_flSimulationTime" or "m_flAnimTime")
        {
            enc = enc with
            {
                VarEncoder = "simtime"
            };
        }

        string type = StripTemplateArgs(field.TypeName);

        return type switch
        {
            "bool" => Bool(enc),
            "uint8" or "byte" => UInt8(enc),
            "uint16" or "ushort" => UInt16(enc),
            "uint32" or "uint" or "CEntityIndex"
                or "CUtlStringToken"
                or "CPlayerSlot" => UInt32(enc),
            "uint64" or "ulong" => UInt64(enc),
            "int8" or "sbyte" => Int8(enc),
            "int16" or "short" => Int16(enc),
            "int32" or "int" => Int32(enc),
            "int64" or "long" => Int64(enc),
            "float32" or "float" or "float64" => Float(enc),
            "Vector" or "VectorWS" => Vec3(enc),
            "Vector2D" => Vec2(enc),
            "Vector4D" => Vec4(enc),
            "QAngle" => QAngle(enc),
            "CHandle" or "CStrongHandle" or "CEntityHandle" => UInt64Raw(),
            // CGlobalSymbol is an interned-string symbol decoded as a length-prefixed UTF-8 string
            // (dfn's CreateDecoder_CGlobalSymbol → ReadStringUtf8), NOT a uint32 hash like
            // CUtlStringToken. Mis-mapping it to UInt32 read 8 bits where the wire carried a full
            // string, mis-aligning the rest of the entity (e.g. m_vecSecondarySkeletonSlotIDs[] on
            // AnimGraph2-era pawns).
            "CUtlSymbolLarge" or "CUtlString" or "char" or "string" or "CGlobalSymbol" => Str(enc),
            // CUtlBinaryBlock (e.g. AnimGraph2SerializedPoseRecipeSlot_t.m_topology): a
            // length-prefixed opaque blob — UVarInt32 byte-count, then that many raw bytes.
            // The generic fallback read only the length varint and left the blob body in the
            // stream, mis-aligning the rest of the packet (root cause of entity-decode
            // corruption on AnimGraph2-era demos). Mirrors dfn's CreateDecoder_CUtlBinaryBlock.
            "CUtlBinaryBlock" => CUtlBinaryBlock(),
            "Color" => UInt32Raw(), // RGBA packed
            // GameTime / GameTime_t: raw 32-bit float (DecodeFloatNoscale), NOT simtime.
            // demofile-net's CreateDecoder_GameTime is unconditionally ReadFloat.
            "GameTime" or "GameTime_t" => (ref b) => b.ReadFloat(),
            "GameTick" => UInt32(enc), // uint alias
            "CNetworkedQuantizedFloat" => Float(enc), // Float() routes bc>=32 to raw ReadFloat; see TryCreateFloat comment.
            // Array element type — fall through to dynamic decode below
            _ => Fallback(field, enc)
        };
    }

    /// <summary>
    ///     Returns a decoder for the wire bits emitted when a length-1 path lands on a nested-object
    ///     descriptor (Ptr / Vector / PolymorphicPtr). These descriptors used to ship with
    ///     <c>Decoder=null</c>, causing the decode loop to silently consume 0 bits while the wire
    ///     carried 1 bit (Ptr isSet) or a UVarInt32 (Vector resize), cascading into bit-misalignment.
    ///     The returned decoder consumes the bits and returns <c>null</c> — we don't track null/size
    ///     state on <see cref="EntityState" /> today; the priority is consuming the right bits so the
    ///     buffer cursor stays aligned for the rest of the packet.
    /// </summary>
    public static FieldDecoder? CreateLengthOneDecoder(RuntimeField field)
    {
        return field.Shape switch
        {
            FieldShape.Ptr => (ref b) =>
            {
                b.ReadOneBit();
                return null!;
            },
            FieldShape.PolymorphicPtr => (ref b) =>
            {
                b.ReadOneBit(); // isSet
                b.ReadUBitVar(); // child class id (variable bits)
                return null!;
            },
            FieldShape.Vector => (ref b) =>
            {
                b.ReadUVarInt32();
                return null!;
            },
            // FixedArray and PlainStruct should not receive length-1 paths on the wire.
            // Return null so the caller (BuildFieldDescs) can leave the descriptor's Decoder
            // unset; if a length-1 path does arrive, the existing `desc.Decoder is not null`
            // guard in ReadEntityFields will skip it (preserving today's behavior for these
            // unexpected cases rather than silently consuming wrong bits).
            _ => null
        };
    }

    /// <summary>
    ///     Returns a typed <see cref="FloatDecoder" /> for float scalar fields, or <c>null</c>
    ///     if the field type requires the generic <see cref="FieldDecoder" /> path.
    /// </summary>
    public static FloatDecoder? TryCreateFloat(RuntimeField field)
    {
        FieldEncodingInfo enc = FieldEncodingInfo.From(field);
        // Schema-attribute overrides: CS2 marks m_flSimulationTime / m_flAnimTime with
        // MNetworkSerializer="simulationTimeSerializer"/"animTimeSerializer" in the C++ schema.
        // The proto's var_encoder_sym is unset, so we must apply the override by field name.
        // demofile-net's codegen handles this via custom decoder methods on the schema class.
        if (enc.VarEncoder is null && field.Name is "m_flSimulationTime" or "m_flAnimTime")
        {
            enc = enc with
            {
                VarEncoder = "simtime"
            };
        }

        string type = StripTemplateArgs(field.TypeName);
        return type switch
        {
            "float32" or "float" => BuildFloatDecoder(enc),
            // GameTime / GameTime_t in CS2 is wire-encoded as a raw 32-bit float (DecodeFloatNoscale),
            // NOT as simtime. demofile-net's CreateDecoder_GameTime is unconditionally ReadFloat.
            "GameTime" or "GameTime_t" => (ref b) => b.ReadFloat(),
            // CNetworkedQuantizedFloat: BuildFloatDecoder already does the right thing for the
            // schema's bc/encoder combinations — bc in (0, 32) → quantized; bc==0 or bc>=32 →
            // raw 32-bit ReadFloat. Routing bc>=32 directly to BuildQuantizedDecoder used to
            // produce a decoder that consumed 32 bits but always returned 0 (Low=High=0 fell
            // out of ResolveQuantizedEncoding's "out of range" early return). Matches dfn's
            // CreateDecoder_float which delegates bc>=32 to DecodeFloatNoscale.
            "CNetworkedQuantizedFloat" => BuildFloatDecoder(enc),
            _ => null
        };
    }

    /// <summary>
    ///     Returns a typed <see cref="IntDecoder" /> for integer scalar fields, or <c>null</c>
    ///     if the field type requires the generic <see cref="FieldDecoder" /> path.
    /// </summary>
    public static IntDecoder? TryCreateInt(RuntimeField field)
    {
        FieldEncodingInfo enc = FieldEncodingInfo.From(field);
        string type = StripTemplateArgs(field.TypeName);
        return type switch
        {
            "bool" => (ref b) => b.ReadOneBit() ? 1 : 0,
            "uint8" or "byte" => (ref b) => (int)b.ReadUVarInt32(),
            "uint16" or "ushort" => (ref b) => (int)b.ReadUVarInt32(),
            "uint32" or "uint" or "CEntityIndex"
                or "CUtlStringToken"
                or "CPlayerSlot" => (ref b) => (int)b.ReadUVarInt32(),
            "int8" or "sbyte" => (ref b) => (sbyte)b.ReadVarInt32(),
            "int16" or "short" => (ref b) => (short)b.ReadVarInt32(),
            "int32" or "int" => (ref b) => b.ReadVarInt32(),
            "GameTick" => (ref b) => (int)b.ReadUVarInt32(),
            _ => null
        };
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    private static FieldDecoder Bool(FieldEncodingInfo _) => (ref b) => b.ReadOneBit();

    private static FloatDecoder BuildFloatDecoder(FieldEncodingInfo enc)
    {
        switch (enc.VarEncoder)
        {
            case "coord": return (ref b) => b.ReadCoord();
            case "simtime": return (ref b) => DecodeSimTime(ref b);
            case "runetime": return (ref b) => DecodeRuneTime(ref b);
        }

        if (enc.BitCount is > 0 and < 32)
        {
            return BuildQuantizedDecoder(enc);
        }

        return (ref b) => b.ReadFloat();
    }

    /// <summary>
    ///     Builds a quantized-float decoder that mirrors demofile-net's <c>QuantizedFloatEncoding</c>
    ///     (MIT). The wire encoding has up to three "shortcut" bits (<c>RoundDown</c>, <c>RoundUp</c>,
    ///     <c>EncodeZero</c>) read before the value bits — when the corresponding flag fires, the
    ///     decoder returns a sentinel value (<c>Low</c>, <c>High</c>, or <c>0</c>) without consuming
    ///     the <c>BitCount</c> value bits. Without this, fields like <c>m_vecX</c> consume 20 bits
    ///     where the wire actually carries only 1 (the EncodeZero shortcut), cascading into
    ///     bit-misalignment for the remainder of the packet.
    /// </summary>
    private static FloatDecoder BuildQuantizedDecoder(FieldEncodingInfo enc)
    {
        QuantizedFloatEncoding e = ResolveQuantizedEncoding(enc);
        return (ref b) => DecodeQuantized(e, ref b);
    }

    private static float DecodeQuantized(QuantizedFloatEncoding e, ref BitBuffer b)
    {
        if ((e.Flags & QuantizedFlags.RoundDown) != 0 && b.ReadOneBit())
        {
            return e.Low;
        }

        if ((e.Flags & QuantizedFlags.RoundUp) != 0 && b.ReadOneBit())
        {
            return e.High;
        }

        if ((e.Flags & QuantizedFlags.EncodeZero) != 0 && b.ReadOneBit())
        {
            return 0f;
        }

        return e.Low + (e.High - e.Low) * b.ReadUBits(e.BitCount) * e.DecMul;
    }

    private static float DecodeRuneTime(ref BitBuffer b)
    {
        uint bits = b.ReadUBits(4);
        return BitConverter.UInt32BitsToSingle(bits);
    }

    private static float DecodeSimTime(ref BitBuffer b)
    {
        // CS2 runs at 64 ticks/second; simtime stores integer tick count as a varint.
        uint ticks = b.ReadUVarInt32();
        return ticks * (1f / 64f);
    }

    // ── CUtlBinaryBlock ─────────────────────────────────────────────────────────

    /// <summary>
    ///     Decodes a <c>CUtlBinaryBlock</c>: a UVarInt32 byte-count followed by that many raw
    ///     bytes. We don't retain the blob (no consumer needs it today) — the point is to consume
    ///     the exact bits so the buffer cursor stays aligned. Mirrors demofile-net's
    ///     <c>CreateDecoder_CUtlBinaryBlock</c> (read length, skip body).
    /// </summary>
    private static FieldDecoder CUtlBinaryBlock() => (ref b) =>
    {
        uint byteCount = b.ReadUVarInt32();
        for (uint i = 0; i < byteCount; i++)
        {
            b.ReadByte();
        }

        return null!;
    };

    // ── Fallback ──────────────────────────────────────────────────────────────

    private static FieldDecoder Fallback(RuntimeField field, FieldEncodingInfo enc)
    {
        // Field-name heuristic (dfn FallbackDecoder.TryCreateHeuristicDecoder):
        // m_e<Upper>... is conventional CS2 enum naming → ReadUVarInt64.
        if (field.Name.Length > 4
            && field.Name.StartsWith("m_e", StringComparison.Ordinal)
            && char.IsUpper(field.Name[3]))
        {
            return (ref b) => b.ReadUVarInt64();
        }

        if (IsEnumType(field.TypeName))
        {
            return (ref b) => b.ReadUVarInt64();
        }

        // Unknown — match dfn's FallbackDecoder default for unrecognised types: ReadUVarInt64.
        // (Was ReadUVarInt32 — would mis-consume bits for any ≥5-byte varint value.)
        return (ref b) => b.ReadUVarInt64();
    }

    // ── Floats ────────────────────────────────────────────────────────────────

    private static FieldDecoder Float(FieldEncodingInfo enc)
    {
        switch (enc.VarEncoder)
        {
            case "coord": return (ref b) => b.ReadCoord();
            case "simtime": return (ref b) => DecodeSimTime(ref b);
            case "runetime": return (ref b) => DecodeRuneTime(ref b);
        }

        if (enc.BitCount is > 0 and < 32)
        {
            return QuantizedFloat(enc);
        }

        return (ref b) => b.ReadFloat();
    }

    private static FieldDecoder Int16(FieldEncodingInfo _) => (ref b) => (short)b.ReadVarInt32();
    private static FieldDecoder Int32(FieldEncodingInfo _) => (ref b) => b.ReadVarInt32();

    private static FieldDecoder Int64(FieldEncodingInfo _) => (ref b) =>
    {
        // zigzag int64: read as uint64 and decode
        ulong v = b.ReadUVarInt64();
        long sv = (long)(v >> 1 ^ (ulong)-(long)(v & 1));
        return sv;
    };

    private static FieldDecoder Int8(FieldEncodingInfo _) => (ref b) => (sbyte)b.ReadVarInt32();

    private static bool IsEnumType(string typeName)
    {
        // CS2 enum names follow one of two patterns:
        //   E-prefix:      ECSUsercmd, EPlayerAnimEvent, ERoundEndReason
        //   MixedCase + _t suffix: MoveType_t, DoorState_t, GamePhase_t
        //     (pure-float typedef aliases like GameTime_t are handled explicitly above)
        // "Flags" suffix: CSPlayerBlockingUseAction_t, etc.
        if (typeName.StartsWith('E'))
        {
            return true;
        }

        if (typeName.Contains("Flags", StringComparison.Ordinal))
        {
            return true;
        }

        // Accept _t suffix only for MixedCase names (contains at least one lower-case letter
        // before the _t), which rules out plain typedefs like "float32_t" while keeping
        // DoorState_t, MoveType_t, etc.
        if (typeName.EndsWith("_t", StringComparison.Ordinal) && typeName.Length > 2)
        {
            string stem = typeName[..^2]; // strip "_t"
            if (stem.Any(char.IsLower))
            {
                return true;
            }
        }

        return false;
    }

    private static FieldDecoder QAngle(FieldEncodingInfo enc)
    {
        // qangle_pitch_yaw: 2 components with BitCount bits each
        if (enc.VarEncoder == "qangle_pitch_yaw")
        {
            int bits = enc.BitCount;
            return (ref b) =>
            {
                float pitch = b.ReadAngle(bits);
                float yaw = b.ReadAngle(bits);
                return new Vector3(pitch, yaw, 0f);
            };
        }

        // qangle_precise: per-component flag + ReadCoordPrecise
        if (enc.VarEncoder == "qangle_precise")
        {
            return (ref b) =>
            {
                bool hasPitch = b.ReadOneBit();
                bool hasYaw = b.ReadOneBit();
                bool hasRoll = b.ReadOneBit();
                float pitch = hasPitch ? b.ReadCoordPrecise() : 0f;
                float yaw = hasYaw ? b.ReadCoordPrecise() : 0f;
                float roll = hasRoll ? b.ReadCoordPrecise() : 0f;
                return new Vector3(pitch, yaw, roll);
            };
        }

        // qangle with fixed bit count
        if (enc.BitCount != 0)
        {
            int bits = enc.BitCount;
            return (ref b) =>
            {
                float pitch = b.ReadAngle(bits);
                float yaw = b.ReadAngle(bits);
                float roll = b.ReadAngle(bits);
                return new Vector3(pitch, yaw, roll);
            };
        }

        // qangle with per-component presence flags
        return (ref b) =>
        {
            bool hasPitch = b.ReadOneBit();
            bool hasYaw = b.ReadOneBit();
            bool hasRoll = b.ReadOneBit();
            float pitch = hasPitch ? b.ReadCoord() : 0f;
            float yaw = hasYaw ? b.ReadCoord() : 0f;
            float roll = hasRoll ? b.ReadCoord() : 0f;
            return new Vector3(pitch, yaw, roll);
        };
    }

    private static FieldDecoder QuantizedFloat(FieldEncodingInfo enc)
    {
        QuantizedFloatEncoding e = ResolveQuantizedEncoding(enc);
        return (ref b) => DecodeQuantized(e, ref b);
    }

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private static ulong ReadFixed64(ref BitBuffer b)
    {
        Span<byte> bytes = stackalloc byte[8];
        b.ReadBitsAsBytes(bytes, 64);
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static QuantizedFloatEncoding ResolveQuantizedEncoding(FieldEncodingInfo enc)
    {
        if (enc.BitCount == 0 || enc.BitCount >= 32)
        {
            return new QuantizedFloatEncoding(0f, 0f, 0f, 32, QuantizedFlags.Unset);
        }

        float low = enc.LowValue ?? 0f;
        float high = enc.HighValue ?? 1f;

        QuantizedFlags flags = ValidateFlags((QuantizedFlags)enc.EncodeFlags, low, high);

        int bitCount = enc.BitCount;
        int steps = 1 << bitCount;
        float offset = 0f;

        if ((flags & QuantizedFlags.RoundDown) != 0)
        {
            offset = (high - low) / steps;
            high -= offset;
        }
        else if ((flags & QuantizedFlags.RoundUp) != 0)
        {
            offset = (high - low) / steps;
            low += offset;
        }

        if ((flags & QuantizedFlags.EncodeIntegers) != 0)
        {
            float delta = Math.Max(1f, high - low);
            int deltaLog2 = (int)Math.Ceiling(Math.Log2(delta));
            int range = 1 << deltaLog2;
            bitCount = Math.Max(bitCount, deltaLog2);
            steps = 1 << bitCount;
            offset = range / (float)steps;
            high = low + range - offset;
        }

        float decMul = 1f / (steps - 1);

        // Strip flags that would round-trip to themselves (matches demofile-net's optimisation:
        // a RoundDown that quantises Low → Low can be removed since the shortcut is unreachable).
        float Quantize(float value)
        {
            if (value < low)
            {
                return low;
            }

            if (value > high)
            {
                return high;
            }

            float highLowMul = high - low == 0f ? (1u << bitCount) - 1 : ((1u << bitCount) - 1) / (high - low);
            uint i = (uint)((value - low) * highLowMul);
            return low + (high - low) * (i * decMul);
        }

        if ((flags & QuantizedFlags.RoundDown) != 0 && Quantize(low) == low)
        {
            flags &= ~QuantizedFlags.RoundDown;
        }

        if ((flags & QuantizedFlags.RoundUp) != 0 && Quantize(high) == high)
        {
            flags &= ~QuantizedFlags.RoundUp;
        }

        if ((flags & QuantizedFlags.EncodeZero) != 0 && Quantize(0f) == 0f)
        {
            flags &= ~QuantizedFlags.EncodeZero;
        }

        return new QuantizedFloatEncoding(low, high, decMul, bitCount, flags);
    }

    private static FieldDecoder Str(FieldEncodingInfo _) =>
        (ref b) => b.ReadStringUtf8();

    private static string StripTemplateArgs(string typeName)
    {
        int lt = typeName.IndexOf('<');
        return lt >= 0 ? typeName[..lt] : typeName;
    }

    private static FieldDecoder UInt16(FieldEncodingInfo _) => (ref b) => (ushort)b.ReadUVarInt32();
    private static FieldDecoder UInt32(FieldEncodingInfo _) => (ref b) => b.ReadUVarInt32();
    private static FieldDecoder UInt32Raw() => (ref b) => b.ReadUVarInt32();

    private static FieldDecoder UInt64(FieldEncodingInfo enc)
    {
        if (enc.VarEncoder == "fixed64")
        {
            return (ref b) => ReadFixed64(ref b);
        }

        return (ref b) => b.ReadUVarInt64();
    }

    private static FieldDecoder UInt64Raw() => (ref b) => b.ReadUVarInt64();
    private static FieldDecoder UInt8(FieldEncodingInfo _) => (ref b) => (byte)b.ReadUVarInt32();

    private static QuantizedFlags ValidateFlags(QuantizedFlags flags, float low, float high)
    {
        if (flags == QuantizedFlags.Unset)
        {
            return flags;
        }

        if (low == 0f && (flags & QuantizedFlags.RoundDown) != 0
            || high == 0f && (flags & QuantizedFlags.RoundUp) != 0)
        {
            flags &= ~QuantizedFlags.EncodeZero;
        }

        if (low == 0f && (flags & QuantizedFlags.EncodeZero) != 0)
        {
            flags |= QuantizedFlags.RoundDown;
            flags &= ~QuantizedFlags.EncodeZero;
        }

        if (high == 0f && (flags & QuantizedFlags.EncodeZero) != 0)
        {
            flags |= QuantizedFlags.RoundUp;
            flags &= ~QuantizedFlags.EncodeZero;
        }

        if (low > 0f || high < 0f)
        {
            flags &= ~QuantizedFlags.EncodeZero;
        }

        if ((flags & QuantizedFlags.EncodeIntegers) != 0)
        {
            flags = QuantizedFlags.EncodeIntegers;
        }

        return flags;
    }

    private static FieldDecoder Vec2(FieldEncodingInfo enc)
    {
        FieldDecoder fd = Float(enc);
        return (ref b) =>
        {
            float x = (float)fd(ref b)!;
            float y = (float)fd(ref b)!;
            return new Vector2(x, y);
        };
    }

    // ── Vectors ───────────────────────────────────────────────────────────────

    private static FieldDecoder Vec3(FieldEncodingInfo enc)
    {
        if (enc.VarEncoder == "normal")
        {
            return (ref b) => { return b.Read3BitNormal(); };
        }

        FieldDecoder fd = Float(enc);
        return (ref b) =>
        {
            float x = (float)fd(ref b)!;
            float y = (float)fd(ref b)!;
            float z = (float)fd(ref b)!;
            return new Vector3(x, y, z);
        };
    }

    private static FieldDecoder Vec4(FieldEncodingInfo enc)
    {
        FieldDecoder fd = Float(enc);
        return (ref b) =>
        {
            float x = (float)fd(ref b)!;
            float y = (float)fd(ref b)!;
            float z = (float)fd(ref b)!;
            float w = (float)fd(ref b)!;
            return new Vector4(x, y, z, w);
        };
    }

    [Flags]
    private enum QuantizedFlags
    {
        Unset = 0,
        RoundDown = 1,
        RoundUp = 2,
        EncodeZero = 4,
        EncodeIntegers = 8
    }

    private readonly record struct QuantizedFloatEncoding(float Low, float High, float DecMul, int BitCount, QuantizedFlags Flags);
}
