#region

using CS2DemoKit.Parser.GameEvents;
using CS2DemoKit.RulesCatalog;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The generator's fold of a view's <c>baked:</c> filters over wire events, which
///     <c>--measure</c> counts a measured view with: the forms an editorial filter uses evaluate
///     against the payload, and a form the fold does not cover fails loudly.
/// </summary>
[Category("Unit")]
public class BakedFilterTests
{
    private static GameEvent Fire(string weapon, int slot = 3, bool silenced = false) =>
        new("weapon_fire", -1, 0, 0, 0, new WeaponFireEvent { UserId = slot, UserIdPawn = 0, Weapon = weapon, Silenced = silenced });

    [Test]
    public async Task InList_Equality_AndConjunction_ReadThePayload()
    {
        Func<GameEvent, bool> grenades = BakedFilter.Compile(
            ["event.Weapon in [\"weapon_hegrenade\", \"weapon_smokegrenade\"]"]);
        await Assert.That(grenades(Fire("weapon_smokegrenade"))).IsTrue();
        await Assert.That(grenades(Fire("weapon_ak47"))).IsFalse();

        Func<GameEvent, bool> both = BakedFilter.Compile(["event.UserId != 0", "not event.Silenced or event.UserId > 5"]);
        await Assert.That(both(Fire("weapon_ak47", slot: 3))).IsTrue();
        await Assert.That(both(Fire("weapon_ak47", slot: 3, silenced: true))).IsFalse();
        await Assert.That(both(Fire("weapon_ak47", slot: 9, silenced: true))).IsTrue();
        await Assert.That(both(Fire("weapon_ak47", slot: 0))).IsFalse();
    }

    [Test]
    public async Task AFormTheFoldDoesNotCover_FailsTheCompile_AndAnUnknownFieldFailsTheRead()
    {
        Assert.Throws<InvalidOperationException>(() => BakedFilter.Compile(["event.UserId + 1 > 2"]));
        Assert.Throws<InvalidOperationException>(() => BakedFilter.Compile(["event.Weapon ="]));
        Func<GameEvent, bool> missing = BakedFilter.Compile(["event.Nope == 1"]);
        Assert.Throws<InvalidOperationException>(() => missing(Fire("weapon_ak47")));
        await Task.CompletedTask;
    }

    /// <summary>Every shipped baked filter folds, so a regen never trips on one at --measure only.</summary>
    [Test]
    public async Task EveryShippedBakedFilter_Compiles()
    {
        int views = 0;
        foreach (Catalog.CatalogView view in Catalog.CatalogResource.Load().Views.Where(v => v.Baked.Count > 0))
        {
            Func<GameEvent, bool> passes = BakedFilter.Compile(view.Baked);
            await Assert.That(passes).IsNotNull().Because(view.Name);
            views++;
        }

        await Assert.That(views).IsGreaterThan(2);
    }
}
