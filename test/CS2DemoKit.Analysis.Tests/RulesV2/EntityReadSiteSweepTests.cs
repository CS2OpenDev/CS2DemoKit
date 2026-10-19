#region

using CS2DemoKit.Analysis.Building;
using CS2DemoKit.Analysis.Catalog;
using CS2DemoKit.Analysis.Config;
using CS2DemoKit.Analysis.Graphs;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Registry;
using CS2DemoKit.Analysis.RulesetsV2.Resolve;
using CS2DemoKit.Analysis.Yaml;

#endregion

namespace CS2DemoKit.Analysis.Tests.RulesV2;

/// <summary>
///     Entity-read SITE SWEEP: does a subject-player entity-provider read
///     (<c>player.health</c>, an <c>entity.pawn.health</c> per-player provider) route to the
///     entity-provider compile seam across every expression site a v2 stat exposes — <c>where:</c>, the
///     value selector (<c>sum:</c>), <c>while:</c>, a <c>flag: when:</c>, and <c>compute:</c>?
///     <para>
///         Pure resolve plus no-demo materialize. A build needs no demo to carry an
///         <c>EntityChangeScanner</c>: an each_player ruleset gets one, and a read that routes to the
///         entity-provider compile seam gates its provider into that scanner. So the <b>PLUMBED</b>
///         marker is the scanner snapshotting <c>entity.pawn.health</c> once the template has
///         materialised (the demo-backed <see cref="WhileEntityGateSubjectBindingTests" /> proves the
///         value read and subject binding). A site that never reaches the entity seam either throws a
///         node-resolution error (<b>BUILD_THROW</b>) or materialises with the provider ungated
///         (<b>CLEAN</b>); both are the structural gap.
///     </para>
///     <para>
///         Confirmed sweep: where:, sum:, while: (folds the entity-bearing gate into the fire-time
///         event condition), when: (the entity read is materialized as a subject-relative
///         <see cref="Nodes.EntityValuePullNode" /> and gated through a multi-source edge) and
///         compute: (the same pull-node, remapped into the node-expression compiler) all report PLUMBED.
///     </para>
/// </summary>
[Category("Unit")]
public class EntityReadSiteSweepTests
{
    private const string Gotv = "Cs2GotvProfile";
    private static readonly CatalogScopeAdapter _adapter = CatalogScopeAdapter.From(CatalogResource.Load());

    // ── Site rulesets (each reads player.health = entity.pawn.health for the subject slot) ──

    private static string WhereSite =>
        """
        ruleset: sweep_where
        for: each_player
        stats:
          hp_kills:
            count: kill
            where: "player.health > 50"
            per: round
        """;

    private static string SumSite =>
        """
        ruleset: sweep_sum
        for: each_player
        stats:
          hp_sum:
            sum: player.health
            on: damage_dealt
            match: { enemy: true }
            per: match
        """;

    private static string WhileSite =>
        """
        ruleset: sweep_while
        for: each_player
        stats:
          healthy_kills:
            count: kill
            while: "player.health > 50"
            per: round
        """;

    private static string WhenSite =>
        """
        ruleset: sweep_when
        for: each_player
        stats:
          healthy_flag:
            flag:
              when: "player.health > 50"
            per: round
        """;

    private static string ComputeSite =>
        """
        ruleset: sweep_compute
        for: each_player
        stats:
          hp_seen:
            count: kill
            per: round
          hp_compute:
            compute: "hp_seen + player.health"
            per: round
        """;

    // Role handle: a non-subject role's entity value (the kill view's victim), read in where:.
    private static string RoleHandleSite =>
        """
        ruleset: sweep_role
        for: each_player
        stats:
          low_hp_victim_kills:
            count: kill
            where: "victim.health > 50"
            per: round
        """;

    private static string IsWalkingSite =>
        """
        ruleset: sweep_is_walking
        for: each_player
        stats:
          walking_kills:
            count: kill
            where: "player.is_walking"
            per: round
        """;

    // The flag bits and the throw-strength hop, one where: site each.
    private static string OnGroundSite => Site("on_ground", "player.on_ground");
    private static string DuckingSite => Site("ducking", "player.ducking");
    private static string FlagsSite => Site("flags", "player.flags > 0");
    private static string ThrowStrengthSite => Site("throw_strength", "player.weapon_throw_strength > 0.5");
    private static string CoachingTeamSite => Site("coaching_team", "player.coaching_team == 0");

    private static string Site(string id, string where) =>
        $$"""
        ruleset: sweep_{{id}}
        for: each_player
        stats:
          {{id}}_kills:
            count: kill
            where: "{{where}}"
            per: round
        """;

    [Test]
    public async Task Where_Site_Probe()
    {
        (string outcome, string detail) = Probe(WhereSite);
        Console.WriteLine($"[sweep] where: player.health outcome={outcome} detail={detail}");
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task Sum_Site_Probe()
    {
        (string outcome, string detail) = Probe(SumSite);
        Console.WriteLine($"[sweep] sum: player.health outcome={outcome} detail={detail}");
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task While_Site_Probe_FixedPlumbsToEntitySeam()
    {
        (string outcome, string detail) = Probe(WhileSite);
        Console.WriteLine($"[sweep] while: player.health outcome={outcome} detail={detail}");
        // Pre-fix this was BUILD_THROW ("when: comparison does not reference a sibling stat/context");
        // the fix folds the entity-bearing while: gate into the fire-time event condition, so it now
        // routes to the entity seam exactly like where:.
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task When_Site_Probe_PlumbsToPullNode()
    {
        (string outcome, string detail) = Probe(WhenSite);
        Console.WriteLine($"[sweep] when: player.health outcome={outcome} detail={detail}");
        // A flag: when: entity read is materialized as a subject-relative EntityValuePullNode and
        // lowered to a MultiSourceConditionalEdge over it, which gates the provider into the scanner
        // exactly as the fire-time where: seam does (the demo-backed WhenEntityGateTests proves the
        // value read and gating).
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task Compute_Site_Probe_PlumbsToPullNode()
    {
        (string outcome, string detail) = Probe(ComputeSite);
        Console.WriteLine($"[sweep] compute: player.health outcome={outcome} detail={detail}");
        // A compute: formula entity read resolves through an EntityValuePullNode registered in
        // localLookup under the read path, remapped into the node-expression compiler exactly as a
        // sibling/context read is, and gates the provider like where: does (the demo-backed
        // ComputeEntityReadTests proves the round-end value read).
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task RoleHandle_B5_Probe_PlumbsToVictimSlot()
    {
        // A non-subject role handle (victim.health on the kill view) maps the role to its event
        // slot-field and emits the event-subject entity grammar `UserId.entity.pawn.health` (no double
        // "entity." prefix). It routes to the same entity seam a where: player.health read uses, the
        // scanner GetPreFrameValue path keyed by the victim's per-fire slot, so the provider is gated.
        (string outcome, string detail) = Probe(RoleHandleSite);
        Console.WriteLine($"[sweep] role-handle victim.health outcome={outcome} detail={detail}");
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(
            "role handles now route to the entity seam via the victim slot: " + detail);
    }

    /// <summary>A bare bool entity read (player.is_walking) routes to the entity seam like player.health.</summary>
    [Test]
    public async Task IsWalking_Site_Probe()
    {
        (string outcome, string detail) = Probe(IsWalkingSite, "entity.pawn.is_walking");
        Console.WriteLine($"[sweep] where: player.is_walking outcome={outcome} detail={detail}");
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    /// <summary>The R7 part 2a reads route to the entity seam under their own provider names.</summary>
    [Test]
    [Arguments("entity.pawn.on_ground")]
    [Arguments("entity.pawn.ducking")]
    [Arguments("entity.pawn.flags")]
    [Arguments("entity.weapon.throw_strength")]
    public async Task FlagsAndThrowStrength_Site_Probe(string provider)
    {
        string yaml = provider switch
        {
            "entity.pawn.on_ground" => OnGroundSite,
            "entity.pawn.ducking" => DuckingSite,
            "entity.pawn.flags" => FlagsSite,
            _ => ThrowStrengthSite
        };
        (string outcome, string detail) = Probe(yaml, provider);
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    /// <summary>The R8 part 2 coach read routes to the entity seam under its own provider name.</summary>
    [Test]
    public async Task CoachingTeam_Site_Probe()
    {
        (string outcome, string detail) = Probe(CoachingTeamSite, "entity.controller.coaching_team");
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    /// <summary>
    ///     Runs the full resolve + no-demo materialize pipeline (entity providers wired) and classifies
    ///     the outcome: LOAD_ERROR, RESOLVE_ERROR (checker rejects), PLUMBED (the build's scanner
    ///     snapshots entity.pawn.health, so the read reached the entity-provider compile seam),
    ///     BUILD_THROW (a materialize throw: a site that never reached the entity seam), or CLEAN (built
    ///     and materialized without the provider ever being gated in).
    /// </summary>
    private static (string Outcome, string Detail) Probe(string yaml, string providerName = "entity.pawn.health")
    {
        RulesetDocumentLoader.Outcome loaded = RulesetDocumentLoader.Load(yaml, "sweep.rules.yaml");
        if (loaded.Doc is null)
        {
            return ("LOAD_ERROR", string.Join("; ", loaded.Diagnostics));
        }

        RulesetResolveResult resolved = CheckedRulesetDraft.Load(loaded.Doc, _adapter).Build(64.0, Gotv);
        if (!resolved.Success)
        {
            return ("RESOLVE_ERROR", string.Join("; ", resolved.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
        }

        BuildResult build;
        try
        {
            RuleChainBuilder builder = new(
                EventRegistry.Build(),
                perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
            build = builder.Build([resolved.Ruleset!]);
            foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
            {
                _ = template.Materialize(0, 2, "sweep-probe");
            }
        }
        catch (Exception ex)
        {
            return ("BUILD_THROW", $"{ex.GetType().Name}: {ex.Message}");
        }

        bool gated = build.EntityScanner?.PerPlayerProviders.Any(p => p.Name == providerName) ?? false;
        return gated
            ? ("PLUMBED", $"resolved + materialized; the scanner snapshots {providerName}")
            : ("CLEAN", $"resolved + materialized without gating {providerName} into the scanner");
    }
}
