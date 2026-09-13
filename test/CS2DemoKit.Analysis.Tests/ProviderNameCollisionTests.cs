#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     The column-identity guards on the per-player provider surface. A digest column is addressed
///     two ways — by INDEX (the layout, the pre-frame snapshot, <c>AimVantageScanner</c>, and the
///     goldens that hash the index) and by NAME (every consumer resolving a column) — so both the
///     order and the uniqueness of provider names are part of the data format rather than
///     bookkeeping.
///     <para>
///         Each test here pins one way the pair used to be able to come apart quietly: a second
///         registration under a taken name replacing the first with no diagnostic and no change in
///         column count, an order that held only as long as nothing was removed from a dictionary,
///         a layout with two identically-named columns of which one is unreachable, and a name
///         comparison that differed between the registry that claims a name and the layout that
///         compares two of them.
///     </para>
/// </summary>
[Category("Unit")]
public class ProviderNameCollisionTests
{
    private static readonly string[] _positionTrio =
        ["entity.pawn.pos_x", "entity.pawn.pos_y", "entity.pawn.pos_z"];

    /// <summary>
    ///     A plugin registering a name the builtins already hold is refused. Without this, the
    ///     column count is unchanged, the layout still compares compatible, and every rule reading
    ///     <c>player.health</c> reads the newcomer.
    /// </summary>
    [Test]
    public async Task Register_RefusesANameTheBuiltinsAlreadyHold()
    {
        PerPlayerEntityValueProviderRegistry registry = PerPlayerEntityValueProviderRegistry.CreateDefault();
        int before = registry.All.Count;

        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => registry.Register(new StubProvider("entity.pawn.health", typeof(int))));

        await Assert.That(thrown.Message).Contains("entity.pawn.health");
        await Assert.That(registry.All.Count).IsEqualTo(before);
        await Assert.That(registry.Get("entity.pawn.health")).IsTypeOf<PawnHealthProvider>()
            .Because("the builtin must still be the provider behind the name");
    }

    /// <summary>
    ///     And under a different casing, because that is the comparison <c>Get</c> resolves with:
    ///     a name that a lookup would find must not be registrable a second time.
    /// </summary>
    [Test]
    public async Task Register_RefusesANameThatDiffersOnlyInCase()
    {
        PerPlayerEntityValueProviderRegistry registry = PerPlayerEntityValueProviderRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(
            () => registry.Register(new StubProvider("Entity.Pawn.Health", typeof(int))));

        await Assert.That(registry.Get("ENTITY.PAWN.HEALTH")).IsTypeOf<PawnHealthProvider>();
    }

    /// <summary>A name nobody holds still registers, and lands last — the column order is append-only.</summary>
    [Test]
    public async Task Register_AppendsAFreeNameInRegistrationOrder()
    {
        PerPlayerEntityValueProviderRegistry registry = new();
        string[] names = ["z.third", "a.first", "m.second"];
        foreach (string name in names)
        {
            registry.Register(new StubProvider(name, typeof(int)));
        }

        await Assert.That(registry.All.Select(p => p.Name).ToArray()).IsEquivalentTo(names)
            .Because("column order is registration order, not the order a hash table happens to enumerate");
    }

    /// <summary>
    ///     The shipped column order is the one the goldens and the index-addressed consumers were
    ///     built against: health first, the position trio last.
    /// </summary>
    [Test]
    public async Task DefaultRegistry_KeepsTheShippedColumnOrder()
    {
        string[] shipped = PerPlayerEntityValueProviderRegistry.CreateDefault().All.Select(p => p.Name).ToArray();

        await Assert.That(shipped[0]).IsEqualTo("entity.pawn.health");
        await Assert.That(shipped[^3..]).IsEquivalentTo(_positionTrio);
        await Assert.That(shipped.Distinct(StringComparer.OrdinalIgnoreCase).Count()).IsEqualTo(shipped.Length)
            .Because("a duplicate among the builtins would make one builtin column unreachable by name");
    }

    /// <summary>
    ///     A layout refuses two columns claiming one name. The pair would look healthy to
    ///     <c>IsCompatibleWith</c> (the count is right) while one of the two is unaddressable.
    /// </summary>
    [Test]
    public async Task Layout_RefusesTwoColumnsUnderOneName()
    {
        ArgumentException thrown = Assert.Throws<ArgumentException>(
            () => DigestColumnLayout.For(
            [
                new StubProvider("entity.pawn.health", typeof(int)),
                new StubProvider("entity.pawn.armor", typeof(int)),
                new StubProvider("Entity.Pawn.Health", typeof(int))
            ]));

        await Assert.That(thrown.Message).Contains("Entity.Pawn.Health");
        await Assert.That(thrown.Message).Contains("column 2");
    }

    /// <summary>
    ///     Layout compatibility uses the same case-insensitive comparison the registry claims names
    ///     with and <c>AimVantageScanner</c> resolves columns with. Three sites, one answer.
    /// </summary>
    [Test]
    public async Task Layout_CompatibilityUsesTheSameComparisonAsNameResolution()
    {
        DigestColumnLayout lower = DigestColumnLayout.For([new StubProvider("entity.pawn.health", typeof(int))]);
        DigestColumnLayout mixed = DigestColumnLayout.For([new StubProvider("Entity.Pawn.Health", typeof(int))]);
        DigestColumnLayout other = DigestColumnLayout.For([new StubProvider("entity.pawn.armor", typeof(int))]);

        await Assert.That(lower.IsCompatibleWith(mixed)).IsTrue()
            .Because("a registry resolves these two spellings to one provider, so the layouts cannot disagree");
        await Assert.That(lower.IsCompatibleWith(other)).IsFalse();
    }

    /// <summary>
    ///     The narrowed value-type set (0.11.0) refuses a provider that compiled against the boxed
    ///     digest, and the message has to carry the migration: a third party reaching this throw is
    ///     reaching it from a constructor, with no other documentation in front of them.
    /// </summary>
    [Test]
    public async Task Layout_UnsupportedValueType_NamesTheFourKindsAndTheMigration()
    {
        NotSupportedException thrown = Assert.Throws<NotSupportedException>(
            () => DigestColumnLayout.For([new StubProvider("plugin.pawn.stamina", typeof(double))]));

        await Assert.That(thrown.Message).Contains("plugin.pawn.stamina");
        await Assert.That(thrown.Message).Contains("int, bool, float and string");
        await Assert.That(thrown.Message).Contains("0.10.0")
            .Because("the message is the only place a consumer of the old package learns why it stopped working");
        await Assert.That(thrown.Message).Contains("float for a double");
    }

    private sealed class StubProvider(string name, Type type) : IPerPlayerEntityValueProvider
    {
        public string EntityClass => "CCSPlayerPawn";

        public string FieldName => name;

        public string Name => name;

        public Type ValueType => type;

        public void CaptureAllSlots(EntityStateLayer layer, Action<int, object> emit) =>
            throw new NotSupportedException();

        public object? ReadForPawn(EntityTracker tracker, CSPlayerPawn pawn) => throw new NotSupportedException();

        public object? Read(EntityStateLayer layer, int playerSlot) => throw new NotSupportedException();
    }
}
