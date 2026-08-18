namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     Derives which of an event's integer fields are player references and which are entity
///     references, so the UI can resolve them to names rather than showing a bare number.
/// </summary>
/// <remarks>
///     <para>
///         This used to match PascalCased property names against a hand-maintained rule table —
///         <c>Userid</c>, <c>Attacker</c>, <c>VictimSlot</c>, and so on. That was guessing from a
///         name, and it silently returned nothing for any field whose name was not on the list.
///     </para>
///     <para>
///         The SDK's records carry the answer directly: every property is tagged with its original
///         KV1 type via <c>[GameEventFieldType]</c>, and <c>player_controller_and_pawn</c> /
///         <c>ehandle</c> say exactly what the name table was trying to infer. Tag first, name
///         table only as a fallback for the handful of fields CS2 declares as a plain integer that
///         nonetheless hold an entity index.
///     </para>
/// </remarks>
internal static class GameEventSemantics
{
    /// <summary>
    ///     KV1 type tag → semantic. These are authoritative: the tag is what the game declares.
    /// </summary>
    private static readonly Dictionary<string, FieldSemantic> _byWireType =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["player_controller_and_pawn"] = FieldSemantic.PlayerUserId,
            ["player_controller"] = FieldSemantic.PlayerUserId,
            ["playercontroller"] = FieldSemantic.PlayerUserId,
            // The engine derives wire keys from the declared TYPE, and a player_pawn field's only
            // key is `<name>_pawn` — a pawn entity handle (key type 8), never a controller slot.
            // Through Sdk 4.0.1 these read an absent key and decoded as 0; 4.1 reads the real key
            // (SDK docs/MIGRATION-4.1.md), so the value resolves like any other entity handle.
            ["player_pawn"] = FieldSemantic.EntityHandle,
            ["ehandle"] = FieldSemantic.EntityHandle
        };

    /// <summary>
    ///     Integer KV1 tags — the gate on the name fallback below, since only a numeric field can
    ///     carry a reference. This is the SDK's tag vocabulary, NOT the five names the retired
    ///     generator emitted: <c>long</c> in particular is an <see cref="int" /> once materialised
    ///     and is what CS2 tags every <c>entindex</c> field with.
    /// </summary>
    private static readonly HashSet<string> _integerWireTypes =
        new(StringComparer.OrdinalIgnoreCase) { "int", "short", "byte", "long", "uint64" };

    /// <summary>
    ///     Name fallback, for fields CS2 tags as a plain integer but which hold an entity index.
    ///     The tag cannot distinguish these, so the name is the only signal available.
    /// </summary>
    private static readonly Dictionary<string, FieldSemantic> _byName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Entityid"] = FieldSemantic.EntityIndex,
            ["Entindex"] = FieldSemantic.EntityIndex,
            ["EntindexKilled"] = FieldSemantic.EntityIndex,
            ["EntindexAttacker"] = FieldSemantic.EntityIndex,
            ["EntindexInflictor"] = FieldSemantic.EntityIndex,
            ["Hostage"] = FieldSemantic.EntityIndex,
            // Player references CS2 declares as bare shorts rather than tagging.
            ["Userid"] = FieldSemantic.PlayerUserId,
            ["Attacker"] = FieldSemantic.PlayerUserId,
            ["Assister"] = FieldSemantic.PlayerUserId,
            ["BotId"] = FieldSemantic.PlayerUserId,
            ["Victimid"] = FieldSemantic.PlayerUserId,
            ["Attackerid"] = FieldSemantic.PlayerUserId,
            ["AvengerId"] = FieldSemantic.PlayerUserId,
            ["AvengedPlayerId"] = FieldSemantic.PlayerUserId,
            ["FunfactPlayer"] = FieldSemantic.PlayerUserId
        };

    /// <summary>Derive semantics for every enrichable field on <paramref name="evt" />.</summary>
    public static IReadOnlyList<(string Field, FieldSemantic Kind)> Derive(GameEvent evt)
    {
        List<(string, FieldSemantic)>? result = null;

        foreach ((string name, _, string wireType) in evt.GetDecodedFields())
        {
            if (_byWireType.TryGetValue(wireType, out FieldSemantic sem))
            {
                // A player_controller_and_pawn field is TWO wire keys, and the SDK keeps the
                // declared tag on both halves: the declared property is the controller slot, its
                // `<Name>Pawn` companion the pawn entity handle. Tag plus name suffix is the
                // SDK-documented discriminator (docs/MIGRATION-4.1.md).
                if (sem == FieldSemantic.PlayerUserId && name.EndsWith("Pawn", StringComparison.Ordinal))
                {
                    sem = FieldSemantic.EntityHandle;
                }

                (result ??= []).Add((name, sem));
                continue;
            }

            // Only numeric fields can carry a reference; a string named "Attacker" is a name.
            if (_integerWireTypes.Contains(wireType)
                && _byName.TryGetValue(name, out FieldSemantic named))
            {
                (result ??= []).Add((name, named));
            }
        }

        return result ?? [];
    }
}
