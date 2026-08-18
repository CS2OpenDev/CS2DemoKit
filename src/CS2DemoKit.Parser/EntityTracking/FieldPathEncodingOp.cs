namespace CS2DemoKit.Parser.EntityTracking;

internal sealed record FieldPathEncodingOp(string Name, int Frequency, FieldPathReader? Reader)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}
