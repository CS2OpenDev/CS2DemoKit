namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     A provider whose value is computed from the pawn rather than read off
///     <see cref="IPerPlayerEntityValueProvider.FieldName" />: schema validation checks that the
///     named leaf exists, not that its wire type matches <see cref="IPerPlayerEntityValueProvider.ValueType" />.
/// </summary>
internal interface IDerivedPawnValue;
