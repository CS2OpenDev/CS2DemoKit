namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Shared immutable boxes for the small integers the entity decoder stores through the
///     <c>object?</c>-typed fallback path.
///     <para>
///         <b>Why this exists.</b> Every field the decoder cannot place in a typed lane is written to
///         <see cref="EntityState.SetFallback" />, whose value parameter is <c>object?</c> — so each
///         decoded <c>int</c> allocates a 24-byte box. Measured on one 279 MB demo: <b>14,303,518</b>
///         such writes, of which <b>99.9%</b> carried a value in <c>[0, 256)</c> — roughly 340 MiB of
///         boxes whose contents repeat a few hundred distinct values. Handing out shared boxes for the
///         cached range removes that allocation entirely; anything outside it boxes as before.
///     </para>
///     <para>
///         <b>Why sharing is safe.</b> A box is immutable — nothing in the codebase unboxes by
///         reference (no <c>Unsafe.Unbox</c>), and <see cref="EntityState.FreezeCopy" /> already
///         documents that lane and fallback values "are themselves treated as immutable ... so a
///         shallow element copy is a correct freeze". The one place object identity is observed is
///         the change-detection fast path in the analysis scanner,
///         <c>ReferenceEquals(newValue, last) || Equals(newValue, last)</c> — sharing can only make
///         the reference arm hit where the <c>Equals</c> arm already would have, so the result is
///         unchanged and the comparison gets cheaper.
///     </para>
///     <para>
///         <b>Range.</b> <c>[-128, 1023]</c>. The negative tail is nearly unused (0.0% measured) but
///         costs 128 slots; the upper bound covers the measured 99.9% plus the <c>[256, 1024)</c>
///         bucket with headroom. The table is ~1152 boxes ≈ 27 KB, allocated once per process.
///     </para>
/// </summary>
internal static class Boxes
{
    private const int Min = -128;
    private const int Count = 1152; // [-128, 1023]

    private static readonly object[] s_ints = CreateInts();

    /// <summary>
    ///     Returns a boxed <paramref name="value" />, shared from the cache when it is in range and
    ///     freshly allocated otherwise. Semantically identical to a plain <c>(object)value</c> cast.
    /// </summary>
    internal static object Int(int value)
    {
        // Single unsigned compare covers both bounds: a value below Min wraps to a huge uint.
        uint index = (uint)(value - Min);
        return index < Count ? s_ints[index] : value;
    }

    private static object[] CreateInts()
    {
        object[] table = new object[Count];
        for (int i = 0; i < Count; i++)
        {
            table[i] = i + Min;
        }

        return table;
    }
}
