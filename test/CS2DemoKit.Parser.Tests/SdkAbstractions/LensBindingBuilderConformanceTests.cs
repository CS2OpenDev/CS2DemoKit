#region

using CS2OpenDev.Sdk.Entities;
using CS2DemoKit.Parser.Entities.Generated;
using CS2DemoKit.Parser.Entities.SchemaLens;
using CS2DemoKit.Parser.Entities.SdkAbstractions;

#endregion

namespace CS2DemoKit.Parser.SdkAbstractions.Tests;

/// <summary>
///     The upstream <c>BindingConformanceTests</c> (SDK#6 manifest invariants) applied to
///     <see cref="LensBindingBuilder" />'s full runtime output: every binding built from the
///     shipped <see cref="GeneratedLensRegistry" /> state must satisfy the same structural
///     rules the upstream emitter's manifests will — density, no duplicate ordinals, resolvable
///     non-shadowing aliases, in-range distinct handle ordinals — validated with the SDK's own
///     shipped <see cref="BindingConformance" /> checker.
/// </summary>
[Category("Unit")]
public class LensBindingBuilderConformanceTests
{
    /// <summary>
    ///     The required whole-set gate: BindingConformance.ThrowIfInvalid over the ENTIRE
    ///     built set for every covered Lens class.
    /// </summary>
    [Test]
    public async Task BuiltSet_PassesBindingConformanceInFull()
    {
        LensState lens = GeneratedLensRegistry.Load();
        IReadOnlyList<EntityClassBinding> bindings = LensBindingBuilder.BuildAll(lens);

        await Assert.That(bindings.Count).IsEqualTo(lens.Classes.Count);

        Exception? thrown = null;
        try
        {
            BindingConformance.ThrowIfInvalid(bindings);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        await Assert.That(thrown).IsNull();
    }

    /// <summary>Every binding also validates individually, reporting all findings when it fails.</summary>
    [Test]
    public async Task EveryBinding_ValidatesIndividually()
    {
        List<string> problems = new();
        foreach (EntityClassBinding binding in LensBindingBuilder.BuildAll(GeneratedLensRegistry.Load()))
        {
            foreach (string problem in BindingConformance.Validate(binding))
            {
                problems.Add($"{binding.EngineClass}: {problem}");
            }
        }

        await Assert.That(string.Join("\n", problems)).IsEqualTo("");
    }

    /// <summary>
    ///     The ordinal space is the ordinal-sorted canonical Lens paths — dense, distinct, and
    ///     in <see cref="StringComparer.Ordinal" /> order, exactly matching the class's active
    ///     Lens field set.
    /// </summary>
    [Test]
    public async Task CanonicalPaths_AreOrdinalSortedAndMatchTheLensFieldSet()
    {
        LensState lens = GeneratedLensRegistry.Load();

        foreach (EntityClassBinding binding in LensBindingBuilder.BuildAll(lens))
        {
            string[] expected = lens.Fields.TryGetValue(binding.EngineClass, out Dictionary<string, FieldRule>? fields)
                ? fields.Keys.Order(StringComparer.Ordinal).ToArray()
                : [];

            await Assert.That(binding.CanonicalPaths.ToArray()).IsEquivalentTo(expected);
            await Assert.That(binding.CanonicalPaths.ToArray())
                .IsEquivalentTo(binding.CanonicalPaths.Order(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>HandleOrdinals name exactly the HandleIndex-transformed Lens fields — spot-checked on the pawn.</summary>
    [Test]
    public async Task HandleOrdinals_NameTheHandleFields()
    {
        LensState lens = GeneratedLensRegistry.Load();
        EntityClassBinding pawn = LensBindingBuilder.Build(lens, "CCSPlayerPawn");

        string[] handlePaths = pawn.HandleOrdinals.Select(o => pawn.CanonicalPaths[o]).Order(StringComparer.Ordinal).ToArray();
        string[] expected = lens.Fields["CCSPlayerPawn"]
            .Where(kv => kv.Value.Transform == LensTransform.HandleIndex)
            .Select(kv => kv.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await Assert.That(handlePaths).IsEquivalentTo(expected);
        await Assert.That(handlePaths).Contains("m_hController");
        await Assert.That(handlePaths).Contains("m_pWeaponServices.m_hActiveWeapon");
    }

    /// <summary>NetName follows the codegen's strip-one-leading-C convention (the EngineToNetName table's rule).</summary>
    [Test]
    [Arguments("CCSPlayerPawn", "CSPlayerPawn")]
    [Arguments("CCSPlayerController", "CSPlayerController")]
    [Arguments("CBasePlayerWeapon", "BasePlayerWeapon")]
    [Arguments("CAK47", "AK47")]
    [Arguments("CC4", "C4")]
    public async Task NetName_FollowsTheCodegenConvention(string engineClass, string expectedNetName)
    {
        await Assert.That(LensBindingBuilder.DeriveNetName(engineClass)).IsEqualTo(expectedNetName);
    }

    /// <summary>
    ///     DVN's AliasMap keeps identity entries (canonical → canonical) as a lookup
    ///     convenience; the builder must exclude them, or every field would shadow itself
    ///     under the contract's alias rules.
    /// </summary>
    [Test]
    public async Task IdentityAliases_AreExcluded()
    {
        foreach (EntityClassBinding binding in LensBindingBuilder.BuildAll(GeneratedLensRegistry.Load()))
        {
            HashSet<string> canonical = new(binding.CanonicalPaths, StringComparer.Ordinal);
            foreach (KeyValuePair<string, string> alias in binding.Aliases)
            {
                await Assert.That(alias.Key).IsNotEqualTo(alias.Value);
                await Assert.That(canonical.Contains(alias.Key)).IsFalse();
            }
        }
    }

    /// <summary>
    ///     The shipped Lens state carries no renames yet (genesis only), so the alias path is
    ///     exercised synthetically: a hand-built LensState with one historical spelling must
    ///     produce a conforming binding whose alias resolves into the ordinal space.
    /// </summary>
    [Test]
    public async Task SyntheticRename_ProducesAResolvableAlias()
    {
        LensState lens = new();
        lens.Classes.Add("CCSPlayerPawn");
        lens.Fields["CCSPlayerPawn"] = new Dictionary<string, FieldRule>
        {
            [SdkTestStates.Origin] = new(WireType.ObjectLane, LensTransform.None),
            ["m_iHealth"] = new(WireType.IntLane, LensTransform.None)
        };
        lens.AliasMap["CCSPlayerPawn"] = new Dictionary<string, string>
        {
            [SdkTestStates.Origin] = SdkTestStates.Origin, // identity — must be excluded
            ["m_iHealth"] = "m_iHealth", // identity — must be excluded
            ["m_vecOrigin"] = SdkTestStates.Origin // genuine historical spelling — must survive
        };

        EntityClassBinding binding = LensBindingBuilder.Build(lens, "CCSPlayerPawn");

        await Assert.That(BindingConformance.Validate(binding).ToArray()).IsEmpty();
        await Assert.That(binding.Aliases.Count).IsEqualTo(1);
        await Assert.That(binding.Aliases["m_vecOrigin"]).IsEqualTo(SdkTestStates.Origin);
    }
}
