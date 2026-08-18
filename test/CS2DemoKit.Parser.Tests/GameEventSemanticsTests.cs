using System.Reflection;

using CS2DemoKit.Parser.GameEvents;

using CS2OpenSchema;

namespace CS2DemoKit.Parser.Tests;

/// <summary>
///     <see cref="GameEvent.GetFieldSemantics" /> battery. Semantics decide whether the UI shows a
///     player's name or a bare integer, and a miss degrades silently — the field just renders as a
///     number. That is exactly how two whole tag families were lost when the parser moved onto
///     <c>CS2OpenDev.Sdk.GameEvents</c>: the SDK reports each field's original KV1 tag, a 13-value
///     vocabulary, where the retired generator had reported one of five names. <c>player_pawn</c>
///     was absent from the tag table, and every <c>entindex</c> field is tagged <c>long</c>, which
///     the name fallback's gate did not list.
/// </summary>
/// <remarks>
///     The drift test at the bottom is the durable half: it reads the tag vocabulary back off the
///     SDK assembly, so a tag added upstream fails here rather than going quiet in the UI.
/// </remarks>
[Category("Unit")]
public class GameEventSemanticsTests
{
    /// <summary>A fire that reports whatever (name, value, tag) tuples a test hands it.</summary>
    private sealed record SyntheticEvent(params (string Name, string Value, string WireType)[] Fields)
        : GameEvent("synthetic", 0, 0, 0, 0)
    {
        public override IReadOnlyList<(string Name, string Value, string WireType)> GetDecodedFields() =>
            Fields;
    }

    private static FieldSemantic SemanticOf(string name, string wireType) =>
        new SyntheticEvent((name, "1", wireType)).GetFieldSemantics()
            .Where(f => f.Field == name)
            .Select(f => f.Kind)
            .DefaultIfEmpty(FieldSemantic.None)
            .First();

    [Test]
    [Arguments("player_controller_and_pawn")]
    [Arguments("player_controller")]
    public async Task ControllerTags_ResolveToPlayerUserId(string tag) =>
        await Assert.That(SemanticOf("UserId", tag)).IsEqualTo(FieldSemantic.PlayerUserId);

    [Test]
    public async Task PlayerPawnTag_IsAHandleNotASlot() =>
        // The engine's only wire key for a player_pawn field is `<name>_pawn` — a pawn entity
        // handle. Through Sdk 4.0.1 the SDK read the absent declared key and these decoded as a
        // constant 0, classified here as slots; 4.1 delivers the real handle (SDK
        // docs/MIGRATION-4.1.md). bomb_pickup, decoy_started, door_open and eight more carry
        // one, still NAMED UserId.
        await Assert.That(SemanticOf("UserId", "player_pawn")).IsEqualTo(FieldSemantic.EntityHandle);

    [Test]
    public async Task ControllerAndPawnCompanion_IsAHandleNotASlot() =>
        // player_controller_and_pawn keeps its tag on BOTH halves; the `<Name>Pawn` companion is
        // the pawn handle, discriminated by tag + name suffix per SDK docs/MIGRATION-4.1.md.
        await Assert.That(SemanticOf("UserIdPawn", "player_controller_and_pawn"))
            .IsEqualTo(FieldSemantic.EntityHandle);

    [Test]
    [Arguments("EntIndex")]
    [Arguments("EntIndexAttacker")]
    [Arguments("EntIndexInflictor")]
    [Arguments("EntIndexKilled")]
    [Arguments("EntityId")]
    public async Task EntityIndexFields_ResolveUnderTheLongTag(string field)
    {
        // CS2 declares these as a plain integer, so only the name identifies them — but it tags
        // that integer `long`, and the name fallback is gated on the field being numeric.
        await Assert.That(SemanticOf(field, "long")).IsEqualTo(FieldSemantic.EntityIndex);
    }

    [Test]
    public async Task StringFieldsNamedLikeAReference_StayUnresolved()
    {
        // The gate's actual job: player_death.Weapon is a string, and OtherDeath names an
        // attacker in prose. Neither is a slot.
        await Assert.That(SemanticOf("Attacker", "string")).IsEqualTo(FieldSemantic.None);
    }

    [Test]
    public async Task EhandleFieldsAreHandlesNotSlots() =>
        await Assert.That(SemanticOf("Target", "ehandle")).IsEqualTo(FieldSemantic.EntityHandle);

    [Test]
    public async Task EveryReferenceBearingTagTheSdkShips_IsClassified()
    {
        // Drift tripwire. `player_*` and `ehandle` name a reference outright — an unclassified one
        // is a silent display regression, not a compile error. Read off the SDK rather than
        // hardcoded, so an upstream addition surfaces here.
        Assembly sdk = typeof(GameEventFieldTypeAttribute).Assembly;
        HashSet<string> referenceTags = [.. sdk.GetTypes()
            .Where(t => t.IsClass && t.Namespace?.Contains("Events", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.GetCustomAttribute<GameEventFieldTypeAttribute>()?.TypeTag)
            .Where(tag => tag is not null && (tag.Contains("player", StringComparison.Ordinal)
                                              || tag.Contains("pawn", StringComparison.Ordinal)
                                              || tag == "ehandle"))
            .Select(tag => tag!)];

        await Assert.That(referenceTags).IsNotEmpty();

        List<string> unclassified = [.. referenceTags
            .Where(tag => SemanticOf("UserId", tag) == FieldSemantic.None)];

        await Assert.That(unclassified).IsEmpty();
    }
}
