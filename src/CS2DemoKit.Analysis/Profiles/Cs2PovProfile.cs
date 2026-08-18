#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     Skeleton profile for first-person POV recordings. POV demos differ
///     from GOTV/HLTV in that many events are observer-relative (only the
///     recording player's perspective is fully populated; other players'
///     hurt/death/blind events may be absent or partial).
/// </summary>
/// <remarks>
///     Currently inherits the GOTV vocabulary verbatim because we don't
///     yet have a POV demo bench to validate against. Override accessors
///     when real POV demos prove a logical event is unavailable.
/// </remarks>
public sealed class Cs2PovProfile : Cs2GotvProfile
{
    /// <inheritdoc />
    public override DemoSourceKind Kind => DemoSourceKind.Pov;
}
