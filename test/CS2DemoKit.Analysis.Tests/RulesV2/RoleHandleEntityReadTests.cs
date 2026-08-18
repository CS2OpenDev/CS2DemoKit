#region

using System.Reflection;
using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     B5 role-handle entity reads: a v2 author under an event view can read a ROLE's
///     entity state — <c>victim.entity.pawn.health</c> (short: <c>victim.health</c>), <c>killer.*</c>,
///     <c>assister.*</c>. The planner must map the role to its event slot-field (<c>victim</c> →
///     <c>UserId</c>) and emit the event-subject entity grammar <c>&lt;SlotField&gt;.&lt;provider&gt;</c>
///     the compiler resolves — NOT the pre-fix <c>{role}.entity.{provider}</c>, which both double-prefixed
///     <c>entity.</c> and never bound the slot.
///     <para>
///         These are pure resolve + string-rewrite assertions: they prove the emitted v1 spelling and the
///         resolved <see cref="EntityProviderReference.RoleSlotField" /> directly, independent of a demo.
///         The demo-independent routing to the entity seam is proven by
///         <see cref="EntityReadSiteSweepTests.RoleHandle_B5_Probe_PlumbsToVictimSlot" />.
///     </para>
/// </summary>
[Category("Unit")]
public class RoleHandleEntityReadTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    private static readonly MethodInfo _rewriteEntityReadsMethod =
        typeof(RuleChainBuilder).GetMethod("RewriteEntityReads",
            BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("RewriteEntityReads not found");

    private static string RoleSite(string roleMember) =>
        $$"""
          ruleset: role_probe
          for: each_player
          stats:
            probe:
              count: kill
              where: "{{roleMember}} > 50"
              per: round
          """;

    /// <summary>Resolves the ruleset and returns the single stat's checked IR (or throws with diagnostics).</summary>
    private static CheckedStat ResolveStat(string yaml)
    {
        RulesetDocumentLoader.Outcome loaded = RulesetDocumentLoader.Load(yaml, "role.rules.yaml");
        if (loaded.Doc is null)
        {
            throw new InvalidOperationException("load failed: " + string.Join("; ", loaded.Diagnostics));
        }

        RulesetResolveResult resolved = CheckedRulesetDraft.Load(loaded.Doc, _adapter).Build(64.0, Gotv);
        if (!resolved.Success)
        {
            throw new InvalidOperationException(
                "resolve failed: " + string.Join("; ", resolved.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        return resolved.Ruleset!.Stats.Single(s => s.StatId == "probe");
    }

    private static string? Rewrite(string expression, CheckedStat stat) =>
        (string?)_rewriteEntityReadsMethod.Invoke(null, [expression, stat]);

    [Test]
    public async Task Victim_Handle_ResolvesSlotFieldAndEmitsEventSubjectForm()
    {
        CheckedStat stat = ResolveStat(RoleSite("victim.health"));

        EntityProviderReference read = stat.EntityReads.Single();
        await Assert.That(read.Subject).IsEqualTo("victim");
        await Assert.That(read.ProviderName).IsEqualTo("entity.pawn.health");
        await Assert.That(read.RoleSlotField).IsEqualTo("UserId");

        string rewritten = Rewrite("victim.health > 50", stat)!;
        // The event-subject entity grammar the compiler resolves via the per-fire slot read.
        await Assert.That(rewritten).IsEqualTo("UserId.entity.pawn.health > 50");
        // The two pre-fix bugs must both be gone: no double "entity.entity", no "victim.entity" role prefix.
        await Assert.That(rewritten).DoesNotContain("entity.entity");
        await Assert.That(rewritten).DoesNotContain("victim.entity");
    }

    [Test]
    public async Task Killer_Handle_EmitsKillerSlotForm()
    {
        CheckedStat stat = ResolveStat(RoleSite("killer.health"));
        EntityProviderReference read = stat.EntityReads.Single();
        await Assert.That(read.RoleSlotField).IsEqualTo("Attacker");
        await Assert.That(Rewrite("killer.health > 50", stat)).IsEqualTo("Attacker.entity.pawn.health > 50");
    }

    [Test]
    public async Task Assister_Handle_EmitsAssisterSlotForm()
    {
        CheckedStat stat = ResolveStat(RoleSite("assister.health"));
        EntityProviderReference read = stat.EntityReads.Single();
        await Assert.That(read.RoleSlotField).IsEqualTo("Assister");
        await Assert.That(Rewrite("assister.health > 50", stat)).IsEqualTo("Assister.entity.pawn.health > 50");
    }

    [Test]
    public async Task Player_Subject_IsUnchanged_Regression()
    {
        // A player.* subject entity read must still emit `player.<provider>` (fixed-slot subject read) —
        // the fix touches only the non-player role path.
        CheckedStat stat = ResolveStat(RoleSite("player.health"));
        EntityProviderReference read = stat.EntityReads.Single();
        await Assert.That(read.Subject).IsEqualTo(EntityProviderReference.PlayerSubject);
        await Assert.That(read.RoleSlotField).IsNull();
        await Assert.That(Rewrite("player.health > 50", stat)).IsEqualTo("player.entity.pawn.health > 50");
    }

    [Test]
    public async Task RoleHandle_UnknownRoleOnView_IsAttributedError()
    {
        // `planter` is not a role on the kill view (killer/victim/assister). It is not a player member,
        // context, or sibling either, so the checker rejects it with an attributed diagnostic rather than
        // silently treating it as an entity read.
        RulesetDocumentLoader.Outcome loaded =
            RulesetDocumentLoader.Load(RoleSite("planter.health"), "role.rules.yaml");
        await Assert.That(loaded.Doc).IsNotNull();

        RulesetResolveResult resolved = CheckedRulesetDraft.Load(loaded.Doc!, _adapter).Build(64.0, Gotv);
        Console.WriteLine("[role] planter.health diagnostics: "
                          + string.Join("; ", resolved.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        await Assert.That(resolved.Success).IsFalse();
    }
}
