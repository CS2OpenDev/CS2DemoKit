#region

using CS2DemoKit.Analysis.Plugins;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     The column plan of a per-pawn digest: one column per per-player provider in registration
///     order, each with its storage kind, its index inside that kind's storage, and its typed cell
///     reader when the provider has one. Built once per digest stream and shared by every row that
///     stream emits, so the extractor's inner loop is an array index per column, not a type test.
///     <para>
///         Two layouts are compatible when their kinds and names agree column for column. That is
///         the test the consumer applies before folding a digest, because the parallel producer's
///         workers each hold their own provider clones and so their own layout instances.
///     </para>
/// </summary>
internal sealed class DigestColumnLayout
{
    private DigestColumnLayout(IReadOnlyList<IPerPlayerEntityValueProvider> providers)
    {
        Providers = providers;
        int count = providers.Count;
        Kinds = count > 0 ? new PawnCellKind[count] : [];
        Names = count > 0 ? new string[count] : [];
        KindIndex = count > 0 ? new int[count] : [];
        IntReaders = count > 0 ? new IPawnIntCellReader?[count] : [];
        FloatReaders = count > 0 ? new IPawnFloatCellReader?[count] : [];
        StringReaders = count > 0 ? new IPawnStringCellReader?[count] : [];
        Words = (count + 63) >> 6;

        HashSet<string> claimed = new(count, StringComparer.OrdinalIgnoreCase);
        for (int p = 0; p < count; p++)
        {
            IPerPlayerEntityValueProvider provider = providers[p];
            PawnCellKind kind = KindOf(provider);
            if (!claimed.Add(provider.Name))
            {
                throw new ArgumentException(
                    $"two providers in this layout are both named '{provider.Name}' (column {p}). A consumer "
                    + "resolves a column by name, so the second would be unreachable and every read of that "
                    + "name would land on the first. Provider names are compared case-insensitively.",
                    nameof(providers));
            }

            Kinds[p] = kind;
            Names[p] = provider.Name;
            switch (kind)
            {
                case PawnCellKind.Int:
                case PawnCellKind.Bool:
                    KindIndex[p] = IntCount++;
                    IntReaders[p] = provider as IPawnIntCellReader;
                    break;
                case PawnCellKind.Float:
                    KindIndex[p] = FloatCount++;
                    FloatReaders[p] = provider as IPawnFloatCellReader;
                    break;
                default:
                    KindIndex[p] = StringCount++;
                    StringReaders[p] = provider as IPawnStringCellReader;
                    break;
            }
        }
    }

    /// <summary>The layout of a digest with no per-player columns.</summary>
    public static DigestColumnLayout Empty { get; } = new([]);

    /// <summary>The providers, in column order.</summary>
    public IReadOnlyList<IPerPlayerEntityValueProvider> Providers { get; }

    /// <summary>Storage kind per column.</summary>
    public PawnCellKind[] Kinds { get; }

    /// <summary>Provider name per column, the identity a consumer resolves columns by.</summary>
    public string[] Names { get; }

    /// <summary>Per column, the index into that column's kind storage (<see cref="PawnCellKind.Bool" /> shares the int storage).</summary>
    public int[] KindIndex { get; }

    /// <summary>Typed int/bool reader per column, or null where the provider is read boxed.</summary>
    public IPawnIntCellReader?[] IntReaders { get; }

    /// <summary>Typed float reader per column, or null where the provider is read boxed.</summary>
    public IPawnFloatCellReader?[] FloatReaders { get; }

    /// <summary>Typed string reader per column, or null where the provider is read boxed.</summary>
    public IPawnStringCellReader?[] StringReaders { get; }

    /// <summary>Number of int-storage cells per row (int and bool columns together).</summary>
    public int IntCount { get; }

    /// <summary>Number of float cells per row.</summary>
    public int FloatCount { get; }

    /// <summary>Number of string cells per row.</summary>
    public int StringCount { get; }

    /// <summary>Number of 64-bit words a per-row column mask needs; zero for the empty layout.</summary>
    public int Words { get; }

    /// <summary>Number of columns.</summary>
    public int Count => Kinds.Length;

    /// <summary>The layout for <paramref name="providers" /> in list order; <see cref="Empty" /> for none.</summary>
    /// <exception cref="NotSupportedException">A provider's value type is outside the four digest kinds.</exception>
    /// <exception cref="ArgumentException">
    ///     Two providers claim the same name. A column's identity to a consumer IS its name, so a
    ///     duplicate makes one of the two columns unaddressable while leaving the column count — and
    ///     therefore <see cref="IsCompatibleWith" /> — looking perfectly healthy.
    /// </exception>
    public static DigestColumnLayout For(IReadOnlyList<IPerPlayerEntityValueProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        return providers.Count == 0 ? Empty : new DigestColumnLayout(providers);
    }

    /// <summary>The storage kind for a provider's declared value type.</summary>
    /// <exception cref="NotSupportedException">The type is outside the four digest kinds.</exception>
    public static PawnCellKind KindOf(IPerPlayerEntityValueProvider provider)
    {
        Type type = provider.ValueType;
        if (type == typeof(int))
        {
            return PawnCellKind.Int;
        }

        if (type == typeof(bool))
        {
            return PawnCellKind.Bool;
        }

        if (type == typeof(float))
        {
            return PawnCellKind.Float;
        }

        if (type == typeof(string))
        {
            return PawnCellKind.String;
        }

        // A third-party provider that declared double, long, uint or an enum compiled and ran
        // against the object?[] digest of 0.10.0 and reaches this throw from the scanner's
        // constructor, so the message has to say what the four kinds are AND how to get back into
        // them — not just that the type is out.
        throw new NotSupportedException(
            $"per-player provider '{provider.Name}' declares value type {type.Name}; the per-pawn digest stores "
            + "int, bool, float and string columns only. Through 0.10.0 the digest held object?[] and accepted any "
            + "value type. Narrow the declaration at the provider: float for a double, int for a long, uint, short "
            + "or an enum's underlying value, bool for a flag, string for anything with no numeric meaning. "
            + "Widening the digest is not the fix — the closed set is what makes the columns unboxed.");
    }

    /// <summary>Whether a digest built on <paramref name="other" /> can be folded by a consumer built on this layout.</summary>
    /// <remarks>
    ///     Names are compared case-insensitively, which is the canonical comparison for a provider
    ///     name across this codebase: <c>PerPlayerEntityValueProviderRegistry</c> claims and
    ///     resolves names that way, and <c>AimVantageScanner.ColumnOf</c> matches them that way. An
    ///     ordinal comparison here would call two layouts incompatible over a difference that no
    ///     lookup can see, and a registry cannot produce that difference anyway because it refuses
    ///     the second spelling.
    /// </remarks>
    public bool IsCompatibleWith(DigestColumnLayout other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (Count != other.Count)
        {
            return false;
        }

        for (int p = 0; p < Count; p++)
        {
            if (Kinds[p] != other.Kinds[p]
                || !string.Equals(Names[p], other.Names[p], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
