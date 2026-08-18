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
///         Pure resolve + no-demo materialize. In a no-demo build there is no
///         <c>EntityChangeScanner</c>, so a read that DID route to
///         <see cref="ExpressionCompiler.CompileEventCondition" /> /
///         <c>CompileEventValueSelector</c> throws a specific "requires per-player entity providers and
///         a player slot" error — the <b>PLUMBED</b> marker (it would resolve the subject's value with a
///         demo; the demo-backed <see cref="WhileEntityGateSubjectBindingTests" /> proves the value read
///         and subject binding). A site that never reaches the entity seam throws a <em>node-resolution</em>
///         error instead (<b>BUILD_THROW</b>) — that is the confirmed structural gap.
///     </para>
///     <para>
///         Confirmed sweep (post-fix): where: PLUMBED, sum: PLUMBED, while: PLUMBED (fixed — folds the
///         entity-bearing gate into the fire-time event condition), when: PLUMBED (fixed — the entity read
///         is materialized as a subject-relative <see cref="Nodes.EntityValuePullNode" /> and gated through
///         a multi-source edge), compute: PLUMBED (fixed — the same pull-node, remapped into the node-
///         expression compiler). Both settle-site reads share the fire-time entity seam's no-demo marker,
///         so a no-demo probe classifies them PLUMBED exactly as where:/sum:/while: do.
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
        // FIXED (settle-site entity pull-node): a flag: when: entity read is now materialized as a
        // subject-relative EntityValuePullNode (B6-style) and lowered to a MultiSourceConditionalEdge over
        // it. In a no-demo build there is no EntityChangeScanner, so the pull-node materialization raises
        // the SAME "requires per-player entity providers and a player slot" marker the fire-time where:
        // seam raises = PLUMBED (the demo-backed WhenEntityGateTests proves the value read + gating).
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task Compute_Site_Probe_PlumbsToPullNode()
    {
        (string outcome, string detail) = Probe(ComputeSite);
        Console.WriteLine($"[sweep] compute: player.health outcome={outcome} detail={detail}");
        // FIXED (settle-site entity pull-node): a compute: formula entity read now resolves through an
        // EntityValuePullNode registered in localLookup under the read path, remapped into the node-
        // expression compiler exactly as a sibling/context read is. In a no-demo build the pull-node
        // materialization raises the SAME entity-seam marker as where: = PLUMBED (the demo-backed
        // ComputeEntityReadTests proves the round-end value read).
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(detail);
    }

    [Test]
    public async Task RoleHandle_B5_Probe_PlumbsToVictimSlot()
    {
        // B5 (FIXED): a non-subject role handle (victim.health on the kill view) now maps the role to its
        // event slot-field and emits the event-subject entity grammar `UserId.entity.pawn.health` (no
        // double "entity." prefix). It routes to the same entity seam a where: player.health read uses —
        // the scanner GetPreFrameValue path, but keyed by the victim's per-fire slot — so in a no-demo
        // build it hits the "requires per-player entity providers and a player slot" throw = PLUMBED.
        (string outcome, string detail) = Probe(RoleHandleSite);
        Console.WriteLine($"[sweep] role-handle victim.health outcome={outcome} detail={detail}");
        await Assert.That(outcome).IsEqualTo("PLUMBED").Because(
            "role handles now route to the entity seam via the victim slot: " + detail);
    }

    /// <summary>
    ///     Runs the full resolve + no-demo materialize pipeline (entity providers wired) and classifies
    ///     the outcome: LOAD_ERROR, RESOLVE_ERROR (checker rejects), PLUMBED (the entity read reached the
    ///     entity-provider compile seam — the no-demo "needs a scanner" throw), BUILD_THROW (any other
    ///     materialize throw — a site that never reached the entity seam), or CLEAN.
    /// </summary>
    private static (string Outcome, string Detail) Probe(string yaml)
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

        try
        {
            RuleChainBuilder builder = new(
                EventRegistry.Build(),
                perPlayerEntityProviders: PerPlayerEntityValueProviderRegistry.CreateDefault());
            BuildResult build = builder.Build([resolved.Ruleset!]);
            foreach (PerPlayerNodeTemplate template in build.Graph.PerPlayerTemplates)
            {
                _ = template.Materialize(0, 2, "sweep-probe", null);
            }
        }
        catch (Exception ex)
        {
            string detail = $"{ex.GetType().Name}: {ex.Message}";
            return ex.Message.Contains("requires per-player entity providers and a player slot",
                StringComparison.Ordinal)
                ? ("PLUMBED", detail)
                : ("BUILD_THROW", detail);
        }

        return ("CLEAN", "resolved + materialized without throwing");
    }
}
