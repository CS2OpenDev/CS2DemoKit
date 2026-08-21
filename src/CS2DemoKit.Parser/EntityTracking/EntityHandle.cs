namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     A networked entity handle: an entity index packed with a serial number.
///     <para>
///         CS2 packs 14 index bits and 10 serial bits, so a networked handle is 24 bits wide.
///         Valve's <c>const.h</c> derives the invalid value from that same arithmetic:
///     </para>
///     <code>
///         #define NUM_NETWORKED_EHANDLE_BITS  (MAX_EDICT_BITS + NUM_NETWORKED_EHANDLE_SERIAL_NUMBER_BITS)
///         #define INVALID_NETWORKED_EHANDLE_VALUE  ((1 &lt;&lt; NUM_NETWORKED_EHANDLE_BITS) - 1)
///     </code>
///     <para>
///         Invalid is therefore all-ones at the SERIALIZED width, not <c>0xFFFFFFFF</c>. For CS2
///         that is <c>0x00FFFFFF</c>, which is what a dead pawn's <c>m_hController</c> reports.
///         Because all-ones is all-ones in its low bits too, every width folds to index
///         <see cref="IndexMask" />, so testing the index catches all of them at once. Testing raw
///         sentinel values only catches the widths you thought to enumerate, which is how the same
///         defect reached three separate call sites before this type existed.
///     </para>
/// </summary>
/// <param name="Value">The packed wire value, width-normalized to 32 bits.</param>
public readonly record struct EntityHandle(uint Value)
{
    /// <summary>Index bits in a CS2 entity handle. Matches Source 2's <c>MAX_EDICT_BITS</c>.</summary>
    public const int IndexBits = 14;

    /// <summary>
    ///     Mask selecting the index bits. Its value is also the reserved invalid index, since
    ///     all-ones at any serialized width folds to exactly this.
    /// </summary>
    public const uint IndexMask = (1u << IndexBits) - 1;

    /// <summary>The entity index this handle names. Only meaningful when <see cref="IsValid" />.</summary>
    public int Index => (int)(Value & IndexMask);

    /// <summary>
    ///     The serial number, used to tell a live entity from a different one recycled into the
    ///     same slot. Not validated on lookup today.
    /// </summary>
    public uint Serial => Value >> IndexBits;

    /// <summary>
    ///     Whether this handle names an entity slot at all.
    ///     <para>
    ///         This is the WIRE rule only: the reserved index is rejected, and nothing else is.
    ///         A zero handle is deliberately not folded in here, because "zero means no live
    ///         handle" is a convention some callers publish as API and others do not. Callers keep
    ///         their own zero handling; only the invalid-index rule is shared.
    ///     </para>
    /// </summary>
    public bool IsValid => (Value & IndexMask) != IndexMask;

    /// <summary>Reads a handle from a signed wire value, folding width with an unchecked cast.</summary>
    public static EntityHandle FromRaw(int value) => new(unchecked((uint)value));

    public override string ToString() =>
        IsValid ? $"Index = {Index}, Serial = {Serial}" : "<invalid>";
}
