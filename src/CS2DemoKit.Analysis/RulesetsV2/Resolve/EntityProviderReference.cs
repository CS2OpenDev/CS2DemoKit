namespace CS2DemoKit.Analysis.RulesetsV2.Resolve;

/// <summary>
///     One entity-provider read a checked v2 node performs. These are the reads the planner lowers to
///     <c>EntityChangeScanner.GetPreFrameValue(provider, subjectSlot)</c> and unions into the
///     scanner's provider gating before the scanner is built. A B5 role-handle read
///     (<c>victim.health</c>) carries the role as its <see cref="Subject" />; a <c>player.*</c>
///     read carries the ruleset player.
/// </summary>
/// <param name="Path">The reference path as it resolved (e.g. <c>player.health</c>, <c>victim.armor</c>).</param>
/// <param name="ProviderName">
///     The catalog provider name the read lowers to (e.g. <c>entity.pawn.health</c>) — the key the
///     planner registers against the scanner.
/// </param>
/// <param name="Subject">
///     Whose slot the pre-frame read is keyed by: <see cref="PlayerSubject" /> for the ruleset
///     player (<c>player.*</c>), or a role name (<c>victim</c> / <c>killer</c> / …) for a B5
///     role-handle read.
/// </param>
/// <param name="RoleSlotField">
///     For a B5 role-handle read only: the view role's event slot-field the role reads through
///     (<c>victim</c> → <c>UserId</c>, resolved at resolve-time from the catalog view's <c>roles</c>
///     table). The planner emits the read as <c>&lt;RoleSlotField&gt;.&lt;ProviderName&gt;</c> so the
///     compiler reads the ROLE's pre-frame entity value per fire (the slot is read from the event field),
///     rather than the ruleset player's. Null for a <see cref="PlayerSubject" /> read (whose slot is the
///     per-player chain's compile-time constant, emitted as <c>player.&lt;ProviderName&gt;</c>).
/// </param>
public sealed record EntityProviderReference(
    string Path,
    string ProviderName,
    string Subject,
    string? RoleSlotField = null)
{
    /// <summary>The <see cref="Subject" /> sentinel for a read keyed by the ruleset's own player.</summary>
    public const string PlayerSubject = "player";
}
