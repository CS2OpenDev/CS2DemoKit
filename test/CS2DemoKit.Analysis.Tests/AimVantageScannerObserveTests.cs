#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Analysis.Visibility;
using CS2DemoKit.Parser.EntityTracking;
using CS2OpenDev.Sdk.Entities;

#endregion

namespace CS2DemoKit.Analysis.Tests;

/// <summary>
///     <see cref="AimVantageScanner.Observe(PerPawnColumns, int)" /> against
///     <see cref="AimVantageScanner.Observe(int, IReadOnlyList{object?})" />: the same rows fed to
///     two scanners, one through the typed columns and one through the boxed row the typed form
///     was transcribed from, must fold to the same vantage set. Pure in-memory; the end-to-end
///     pin is <c>AimShotContextDemoTests</c>, which cannot tell which overload a divergence came
///     from.
///     <para>
///         The cases are the twin's branches: a full row, partial rows folding onto prior state, an
///         int cell read as a float, a bool or string cell that counts as seen without moving the
///         value, and a column the digest row does not carry at all.
///     </para>
/// </summary>
[Category("Unit")]
public class AimVantageScannerObserveTests
{
    private const int Tick = 640;

    private static readonly string[] SixNames =
    [
        AimVantageScanner.PosXProvider,
        AimVantageScanner.PosYProvider,
        AimVantageScanner.PosZProvider,
        AimVantageScanner.EyePitchProvider,
        AimVantageScanner.EyeYawProvider,
        AimVantageScanner.DuckAmountProvider
    ];

    [Test]
    public async Task FullRow_FoldsToTheSameVantage()
    {
        DigestColumnLayout layout = FloatLayout(SixNames);
        (int, object?[])[] rows = [(3, [10f, 20f, 30f, -5f, 90f, 0.5f])];

        await AssertTwins(SixNames, [(layout, rows)], 1);
    }

    [Test]
    public async Task PartialRows_FoldOntoPriorState_Identically()
    {
        DigestColumnLayout layout = FloatLayout(SixNames);
        (int, object?[])[] first = [(1, [1f, 2f, 3f, 4f, 5f, 0f]), (2, [7f, 8f, 9f, 10f, 11f, 1f])];
        (int, object?[])[] second = [(1, [1.5f, null, null, null, 6f, null]), (2, [null, null, null, null, null, 0.25f])];
        (int, object?[])[] third = [(2, [null, 8.5f, null, null, null, null])];

        await AssertTwins(SixNames, [(layout, first), (layout, second), (layout, third)], 2);
    }

    [Test]
    public async Task IntCells_ReadAsFloats_InBothArms()
    {
        DigestColumnLayout layout = Layout(
            (AimVantageScanner.PosXProvider, typeof(int)),
            (AimVantageScanner.PosYProvider, typeof(int)),
            (AimVantageScanner.PosZProvider, typeof(float)),
            (AimVantageScanner.EyePitchProvider, typeof(float)),
            (AimVantageScanner.EyeYawProvider, typeof(int)),
            (AimVantageScanner.DuckAmountProvider, typeof(float)));
        (int, object?[])[] rows = [(4, [100, -200, 30f, 12f, 45, 1f])];

        await AssertTwins(SixNames, [(layout, rows)], 1);
    }

    [Test]
    public async Task BoolAndStringCells_CountAsSeen_WithoutMovingTheValue_InBothArms()
    {
        // A bool pitch and a string yaw are nonsense a real registry never produces, but both
        // Fold overloads accept them the same way: the slot gains HasPitch/HasYaw and the
        // numbers stay at their defaults, so a vantage is built with a zero view angle.
        DigestColumnLayout layout = Layout(
            (AimVantageScanner.PosXProvider, typeof(float)),
            (AimVantageScanner.PosYProvider, typeof(float)),
            (AimVantageScanner.PosZProvider, typeof(float)),
            (AimVantageScanner.EyePitchProvider, typeof(bool)),
            (AimVantageScanner.EyeYawProvider, typeof(string)),
            (AimVantageScanner.DuckAmountProvider, typeof(bool)));
        (int, object?[])[] rows = [(5, [1f, 2f, 3f, true, "yaw", true])];

        IReadOnlyList<AimVantage> typed = await AssertTwins(SixNames, [(layout, rows)], 1);
        await Assert.That(typed[0].EyePitchDeg).IsEqualTo(0f);
        await Assert.That(typed[0].EyeYawDeg).IsEqualTo(0f);
        await Assert.That(typed[0].Vantage.Duck).IsEqualTo(0f);
    }

    [Test]
    public async Task ColumnBeyondTheRow_IsSkipped_InBothArms()
    {
        // The scanner resolved duck to column 6 of a seven-name digest order, but the rows it is
        // fed carry six columns. Both overloads skip the column; the vantage stands at duck 0.
        string[] sevenNames =
        [
            AimVantageScanner.PosXProvider,
            AimVantageScanner.PosYProvider,
            AimVantageScanner.PosZProvider,
            AimVantageScanner.EyePitchProvider,
            AimVantageScanner.EyeYawProvider,
            "entity.pawn.filler",
            AimVantageScanner.DuckAmountProvider
        ];
        DigestColumnLayout layout = FloatLayout(sevenNames[..6]);
        (int, object?[])[] rows = [(6, [1f, 2f, 3f, 4f, 5f, 99f])];

        IReadOnlyList<AimVantage> typed = await AssertTwins(sevenNames, [(layout, rows)], 1);
        await Assert.That(typed[0].Vantage.Duck).IsEqualTo(0f);
    }

    // Feeds every frame to a typed-fed scanner and a boxed-fed scanner and compares their samples.
    private static async Task<IReadOnlyList<AimVantage>> AssertTwins(
        IReadOnlyList<string> names,
        IReadOnlyList<(DigestColumnLayout Layout, (int Slot, object?[] Values)[] Rows)> frames,
        int expectedVantages)
    {
        AimVantageScanner typedScanner = new(names, static _ => 2);
        AimVantageScanner boxedScanner = new(names, static _ => 2);

        foreach ((DigestColumnLayout layout, (int Slot, object?[] Values)[] rows) in frames)
        {
            PerPawnColumns columns = PerPawnColumns.FromBoxedRows(layout, rows);
            for (int r = 0; r < columns.Count; r++)
            {
                typedScanner.Observe(columns, r);
            }

            foreach ((int slot, object?[] values) in rows)
            {
                boxedScanner.Observe(slot, values);
            }
        }

        List<AimVantage> typed = typedScanner.Sample(Tick).ToList();
        List<AimVantage> boxed = boxedScanner.Sample(Tick).ToList();

        await Assert.That(typed.Count).IsEqualTo(expectedVantages)
            .Because("the comparison must cover a vantage per fed slot, not an empty sample");
        await Assert.That(boxed.Count).IsEqualTo(typed.Count);
        for (int i = 0; i < typed.Count; i++)
        {
            await Assert.That(typed[i]).IsEqualTo(boxed[i])
                .Because($"slot {typed[i].Vantage.Slot}: the typed fold must produce what the boxed fold produces");
        }

        return typed;
    }

    private static DigestColumnLayout FloatLayout(IReadOnlyList<string> names) =>
        Layout(names.Select(n => (n, typeof(float))).ToArray());

    private static DigestColumnLayout Layout(params (string Name, Type Type)[] columns) =>
        DigestColumnLayout.For(columns.Select(c => (IPerPlayerEntityValueProvider)new StubProvider(c.Name, c.Type)).ToList());

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
