#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     Skeleton profile for FACEIT match recordings. FACEIT demos are
///     GOTV-derived and currently treated as such; this class exists to
///     reserve the source-kind classification slot and to make it cheap to
///     diverge later if FACEIT-specific event differences emerge.
/// </summary>
/// <remarks>
///     No overrides yet. When real FACEIT demos surface a divergence (e.g.
///     additional anti-cheat events or restricted event sets), override the
///     relevant logical-event accessors here.
/// </remarks>
public sealed class Cs2FaceitProfile : Cs2GotvProfile
{
    /// <inheritdoc />
    public override DemoSourceKind Kind => DemoSourceKind.Faceit;
}
