namespace CS2DemoKit.Analysis.GoldenStats;

/// <summary>
///     Lightweight input contract that <see cref="OursGoldenStatsConverter" />
///     accepts as its per-player payload. Decouples the converter from
///     AnalysisBench's internal <c>PlayerReport</c> shape: any caller —
///     bench tool, ad-hoc script, future test scaffold — can construct
///     this without dragging in bench-specific types.
///     <para>
///         <c>Stats</c> values may be <c>int</c>, <c>double</c>, <c>string</c>,
///         <c>bool</c>, or <c>null</c>. The converter coerces every value into
///         the canonical <c>double?</c> shape used in the golden schema.
///     </para>
/// </summary>
public sealed record PlayerStatsInput(
    string Name,
    int Team,
    int PlayerSlot,
    IReadOnlyDictionary<string, object?> Stats);
