namespace CS2DemoKit.Parser.EntityTracking;

internal readonly record struct NodePriority(int Weight, int Value) : IComparable<NodePriority>
{
    /// <inheritdoc />
    public int CompareTo(NodePriority other) => Weight == other.Weight
        ? other.Value.CompareTo(Value)
        : Weight.CompareTo(other.Weight);
}
