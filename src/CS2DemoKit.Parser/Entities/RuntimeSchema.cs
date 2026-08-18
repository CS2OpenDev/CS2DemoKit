namespace CS2DemoKit.Parser.Entities;

/// <summary>
///     Parsed representation of the <c>CSVCMsg_FlattenedSerializer</c> message embedded in every CS2 demo.
///     This schema describes every entity class, its fields, field types, and bit-level encoding parameters.
/// </summary>
public sealed class RuntimeSchema
{
    private RuntimeSchema(string[] symbols, Dictionary<(string, int), RuntimeSerializer> serializers)
    {
        Symbols = symbols;
        Serializers = serializers;
    }

    /// <summary>All serializers keyed by (name, version).</summary>
    public IReadOnlyDictionary<(string Name, int Version), RuntimeSerializer> Serializers { get; }

    /// <summary>Symbol string table: all type/field/encoder names are stored here by index.</summary>
    public string[] Symbols { get; }

    /// <summary>Looks up a serializer by name (uses version 0 if multiple versions exist).</summary>
    public RuntimeSerializer? GetSerializer(string name)
    {
        if (Serializers.TryGetValue((name, 0), out RuntimeSerializer? s))
        {
            return s;
        }

        foreach (KeyValuePair<(string Name, int Version), RuntimeSerializer> kvp in Serializers)
        {
            if (kvp.Key.Name == name)
            {
                return kvp.Value;
            }
        }

        return null;
    }

    // ── Factory ───────────────────────────────────────────────────────────────

    /// <summary>Parse.</summary>
    public static RuntimeSchema Parse(CSVCMsg_FlattenedSerializer msg)
    {
        string[] symbols = msg.Symbols.ToArray();

        // First pass: build all leaf fields from the flat field array
        RuntimeField[] allFields = new RuntimeField[msg.Fields.Count];
        for (int i = 0; i < msg.Fields.Count; i++)
        {
            allFields[i] = ParseField(msg.Fields[i], symbols);
        }

        // Second pass: build serializers (we need all leaf fields first so child references resolve)
        Dictionary<(string, int), RuntimeSerializer> serializers = new();
        foreach (ProtoFlattenedSerializer_t? s in msg.Serializers)
        {
            string serName = Sym(symbols, s.SerializerNameSym);
            int version = s.SerializerVersion;

            // Resolve each field by index into the flat field array
            RuntimeField[] fields = new RuntimeField[s.FieldsIndex.Count];
            for (int i = 0; i < s.FieldsIndex.Count; i++)
            {
                fields[i] = allFields[s.FieldsIndex[i]];
            }

            serializers[(serName, version)] = new RuntimeSerializer(serName, version, fields);
        }

        // Third pass: patch child serializer references on fields that reference nested types
        // (We defer this so all serializers exist before we try to look them up.)
        foreach (RuntimeField field in allFields)
        {
            field.ResolveChildSerializer(serializers);
        }

        return new RuntimeSchema(symbols, serializers);
    }

    private static RuntimeField ParseField(ProtoFlattenedSerializerField_t f, string[] symbols)
    {
        string? childSerName = f.HasFieldSerializerNameSym ? Sym(symbols, f.FieldSerializerNameSym) : null;
        int? childSerVersion = f.HasFieldSerializerVersion ? f.FieldSerializerVersion : null;

        // SendNode is a single symbol containing the dot-separated descent (e.g. "m_Stats.m_Inner").
        // Empty array when unset or when symbol is "(root)" / empty.
        string[] sendNode;
        if (f.HasSendNodeSym)
        {
            string raw = Sym(symbols, f.SendNodeSym);
            sendNode = raw.Length == 0
                ? Array.Empty<string>()
                : raw.Split('.', StringSplitOptions.RemoveEmptyEntries);
        }
        else
        {
            sendNode = Array.Empty<string>();
        }

        // PolymorphicTypes: list of (name, version) pairs the polymorphic Ptr can resolve to.
        IReadOnlyList<(string Name, int Version)> polymorphicTypes;
        if (f.PolymorphicTypes.Count > 0)
        {
            (string, int)[] arr = new (string, int)[f.PolymorphicTypes.Count];
            for (int i = 0; i < f.PolymorphicTypes.Count; i++)
            {
                ProtoFlattenedSerializerField_t.Types.polymorphic_field_t pf = f.PolymorphicTypes[i];
                arr[i] = (Sym(symbols, pf.PolymorphicFieldSerializerNameSym), pf.PolymorphicFieldSerializerVersion);
            }

            polymorphicTypes = arr;
        }
        else
        {
            polymorphicTypes = Array.Empty<(string, int)>();
        }

        string? varSerializerName = f.HasVarSerializerSym ? Sym(symbols, f.VarSerializerSym) : null;

        return new RuntimeField(
            Sym(symbols, f.VarNameSym),
            Sym(symbols, f.VarTypeSym),
            f.HasVarEncoderSym ? Sym(symbols, f.VarEncoderSym) : null,
            f.HasBitCount ? f.BitCount : 0,
            f.HasLowValue ? f.LowValue : null,
            f.HasHighValue ? f.HighValue : null,
            f.HasEncodeFlags ? f.EncodeFlags : 0,
            childSerName,
            childSerVersion ?? 0,
            sendNode,
            polymorphicTypes,
            varSerializerName
        );
    }

    private static string Sym(string[] symbols, int idx) =>
        idx >= 0 && idx < symbols.Length ? symbols[idx] : $"<sym{idx}>";
}

// ── RuntimeSerializer ─────────────────────────────────────────────────────────

// ── RuntimeField ──────────────────────────────────────────────────────────────
