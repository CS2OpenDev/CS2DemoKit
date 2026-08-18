#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     Synthetic <see cref="EntityState" /> fabrication for the SDK#6 (entity abstraction)
///     conformance ports — no demo parsing anywhere. States are built the way the tracker
///     builds them: an internal-ctor <see cref="EntityState" />, a
///     <see cref="ClassShapeBuilder" />-produced shape bound via
///     <see cref="EntityState.BindShape" />, and lane/fallback writes through the same
///     internal setters the decoder uses (visible here via <c>InternalsVisibleTo</c> — no
///     production test hooks were added for this suite).
///     <para>
///         The pawn fixture mirrors the upstream <c>ReadContractTests</c> binding
///         field-for-field, with each field placed on the lane DVN's decoder would really
///         choose: ints and wire-bools on the int lane; vectors, angles, wide ints and
///         handles boxed on the object lane (the honour-the-wire rule).
///     </para>
/// </summary>
internal static class SdkTestStates
{
    internal const string Origin = "m_CBodyComponent.m_pSceneNode.m_vecOrigin";

    /// <summary>The upstream conformance suite's binding, verbatim.</summary>
    internal static EntityClassBinding PawnBinding() => new(
        EngineClass: "CCSPlayerPawn",
        NetName: "CSPlayerPawn",
        CanonicalPaths: ["m_ArmorValue", Origin, "m_angEyeAngles", "m_lifeState", "m_hOwnerEntity", "m_steamID", "m_bSpotted"],
        Aliases: new Dictionary<string, string> { ["m_vecOrigin"] = Origin },
        HandleOrdinals: [4]);

    /// <summary>
    ///     The pawn shape as a current demo's serializer walk would build it — storage keyed
    ///     by the canonical (current) wire spelling.
    /// </summary>
    internal static ClassShape PawnShape()
    {
        ClassShapeBuilder b = new("CCSPlayerPawn");
        b.Allocate(LaneKind.Int, "m_ArmorValue");
        b.Allocate(LaneKind.Object, Origin);
        b.Allocate(LaneKind.Object, "m_angEyeAngles"); // QAngle wire → boxed Vector3(pitch, yaw, roll)
        b.Allocate(LaneKind.Int, "m_lifeState");
        b.Allocate(LaneKind.Object, "m_hOwnerEntity"); // CHandle wire → boxed ulong (honour the wire)
        b.Allocate(LaneKind.Object, "m_steamID"); // uint64 wire → boxed ulong
        b.Allocate(LaneKind.Int, "m_bSpotted"); // bool wire → int 0/1
        return b.Build();
    }

    /// <summary>
    ///     The same pawn as recorded before the (synthetic) rename — the wire spells the
    ///     origin field <c>m_vecOrigin</c>, so storage is keyed by the alias spelling.
    /// </summary>
    internal static ClassShape OldDemoPawnShape()
    {
        ClassShapeBuilder b = new("CCSPlayerPawn");
        b.Allocate(LaneKind.Int, "m_ArmorValue");
        b.Allocate(LaneKind.Object, "m_vecOrigin");
        b.Allocate(LaneKind.Object, "m_angEyeAngles");
        b.Allocate(LaneKind.Int, "m_lifeState");
        b.Allocate(LaneKind.Object, "m_hOwnerEntity");
        b.Allocate(LaneKind.Object, "m_steamID");
        b.Allocate(LaneKind.Int, "m_bSpotted");
        return b.Build();
    }

    /// <summary>Fresh pawn state with <paramref name="shape" /> bound (none received yet).</summary>
    internal static EntityState NewPawn(ClassShape? shape = null)
    {
        EntityState state = new("CCSPlayerPawn", serial: 1);
        state.BindShape(shape ?? PawnShape());
        return state;
    }

    /// <summary>Fresh state with NO shape bound — the tracker's all-fallback mode.</summary>
    internal static EntityState NewShapelessPawn() => new("CCSPlayerPawn", serial: 1);

    /// <summary>
    ///     Writes <paramref name="value" /> the way the decoder would: through the mapped lane
    ///     slot when the shape knows the path, through the fallback dictionary otherwise.
    /// </summary>
    internal static void Write(EntityState state, string path, object? value)
    {
        if (state.Shape is { } shape && shape.PathToSlot.TryGetValue(path, out SlotAddr addr))
        {
            switch (addr.Lane)
            {
                case LaneKind.Int:
                    state.SetIntSlot(addr.Slot, (int)value!);
                    return;
                case LaneKind.Float:
                    state.SetFloatSlot(addr.Slot, (float)value!);
                    return;
                case LaneKind.Object:
                    state.SetObjectSlot(addr.Slot, value);
                    return;
            }
        }

        state.SetFallback(path, value);
    }
}
