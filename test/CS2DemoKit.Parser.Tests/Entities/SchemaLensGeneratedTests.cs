#region

using System.Security.Cryptography;
using System.Text;
using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;

#endregion

namespace CS2DemoKit.Parser.Entities.Tests;

/// <summary>
///     Tests for the codegen-emitted <see cref="GeneratedLensRegistry" />, which is DERIVED
///     from the pinned <c>CS2OpenDev.Sdk.Entities</c> package (bindings + the SDK's
///     <c>schema-lens/state.json</c>) — the local migration JSONs retired 2026-08-15.
///     <list type="bullet">
///         <item>
///             <b>Emit-drift test.</b> Recomputes the canonical-form hash from
///             <see cref="GeneratedLensRegistry.Load" /> and asserts it equals the embedded
///             <see cref="GeneratedLensRegistry.LensHash" /> — a mistyped literal or stale
///             emit fails here in CI.
///         </item>
///         <item>
///             <b>Census pins</b> (measured, adapt on SDK pin bumps): 61 classes / 735 rules
///             at Sdk.Entities 1.1.0 — the full SDK curation, prefix-flattened per concrete
///             class, so formerly dict-only fields (weapon Clip1 etc.) now lane-bind.
///         </item>
///         <item>
///             <b>Storage-policy spot checks</b>: the honour-the-wire mapping and the
///             wire-flattening alias for the origin cell leaves (interim until upstream
///             ships the alias — see the deriver).
///         </item>
///     </list>
/// </summary>
[Category("Unit")]
public class SchemaLensGeneratedTests
{
    // Measured at CS2OpenDev.Sdk.Entities 1.1.0 — adapt deliberately on pin bumps.
    private const int ExpectedClasses = 61;
    private const int ExpectedRules = 735;

    [Test]
    [Category("Smoke")]
    public async Task GeneratedLensRegistry_HashRoundTrips()
    {
        LensState state = GeneratedLensRegistry.Load();
        string recomputed = SchemaLensCanonicalForm.ComputeHash(state);

        await Assert.That(recomputed).IsEqualTo(GeneratedLensRegistry.LensHash);
        await Assert.That(state.CanonicalHash).IsEqualTo(GeneratedLensRegistry.LensHash);
    }

    /// <summary>
    ///     The hash is an identity for a shape, so it must not move with the host. This is the
    ///     property the round-trip above cannot see: CI is Linux, where <c>AppendLine</c> emitted
    ///     the right separator by accident and the same code hashed differently on Windows.
    /// </summary>
    [Test]
    [Category("Smoke")]
    public async Task CanonicalForm_UsesLfRegardlessOfHost()
    {
        string canonical = SchemaLensCanonicalForm.Serialize(GeneratedLensRegistry.Load());

        await Assert.That(canonical).DoesNotContain("\r")
            .Because("the separator belongs to the format, not to Environment.NewLine");
        await Assert.That(canonical).Contains("\n");

        // Pins the CRLF form as a distinct hash, so a future AppendLine cannot pass by producing
        // LF on the machine that happens to run the suite.
        string crlf = canonical.Replace("\n", "\r\n", StringComparison.Ordinal);
        await Assert.That(Sha256Of(crlf)).IsNotEqualTo(GeneratedLensRegistry.LensHash);
        await Assert.That(Sha256Of(canonical)).IsEqualTo(GeneratedLensRegistry.LensHash);
    }

    private static string Sha256Of(string s) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [Test]
    [Category("Smoke")]
    public async Task GeneratedLensRegistry_CensusMatchesThePinnedSdk()
    {
        LensState state = GeneratedLensRegistry.Load();

        await Assert.That(state.Classes.Count).IsEqualTo(ExpectedClasses);
        await Assert.That(state.Fields.Values.Sum(d => d.Count)).IsEqualTo(ExpectedRules);

        // Every class with rules is an active class, and every rule's canonical resolves
        // through its own AliasMap (self-alias invariant the resolver depends on).
        foreach ((string cls, Dictionary<string, FieldRule> fields) in state.Fields)
        {
            await Assert.That(state.Classes.Contains(cls)).IsTrue();
            foreach (string canonical in fields.Keys)
            {
                await Assert.That(state.AliasMap[cls].GetValueOrDefault(canonical))
                    .IsEqualTo(canonical);
            }
        }
    }

    [Test]
    public async Task StoragePolicy_SpotChecks()
    {
        LensState state = GeneratedLensRegistry.Load();

        // Plain int scalar.
        FieldRule health = state.Fields["CCSPlayerPawn"]["m_iHealth"];
        await Assert.That(health.WireType).IsEqualTo(WireType.IntLane);
        await Assert.That(health.Transform).IsEqualTo(LensTransform.None);

        // Bool: honest int lane (bools live as Int32 on the wire); no wrapper-era transform.
        FieldRule defusing = state.Fields["CCSPlayerPawn"]["m_bIsDefusing"];
        await Assert.That(defusing.WireType).IsEqualTo(WireType.IntLane);
        await Assert.That(defusing.Transform).IsEqualTo(LensTransform.None);

        // Handle: honest object lane (boxed raw wire handle) with the HandleIndex marker.
        FieldRule activeWeapon = state.Fields["CCSPlayerPawn"]["m_pWeaponServices.m_hActiveWeapon"];
        await Assert.That(activeWeapon.WireType).IsEqualTo(WireType.ObjectLane);
        await Assert.That(activeWeapon.Transform).IsEqualTo(LensTransform.HandleIndex);

        // Prefix layout reaches inherited fields on concrete classes: CAK47 lanes the
        // base Clip1 — dict-only before the derivation (base-class rules never bound
        // to concrete serializers).
        FieldRule clip = state.Fields["CAK47"]["m_iClip1"];
        await Assert.That(clip.WireType).IsEqualTo(WireType.IntLane);

        // Position cell leaf: int lane (nullability truth is the seen bits, not the rule),
        // and the engine's flat wire spelling resolves through the wire-flattening alias to
        // the schema-true canonical the SDK curates.
        const string cellCanonical = "m_CBodyComponent.m_pSceneNode.m_vecOrigin.m_cellX";
        FieldRule cellX = state.Fields["CCSPlayerPawn"][cellCanonical];
        await Assert.That(cellX.WireType).IsEqualTo(WireType.IntLane);
        await Assert.That(state.AliasMap["CCSPlayerPawn"].GetValueOrDefault("CBodyComponent.m_cellX"))
            .IsEqualTo(cellCanonical);
    }
}
