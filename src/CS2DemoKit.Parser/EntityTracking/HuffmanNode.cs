namespace CS2DemoKit.Parser.EntityTracking;

// Adapted from demofile-net (MIT): https://github.com/saul/demofile-net

internal sealed record HuffmanNode<T>(T? Symbol, int Frequency, HuffmanNode<T>? Left, HuffmanNode<T>? Right)
{
    /// <summary>Builds a canonical Huffman tree from symbol → frequency pairs using a priority queue.</summary>
    public static HuffmanNode<T> Build(IEnumerable<KeyValuePair<T, int>> symbolFreqs)
    {
        PriorityQueue<HuffmanNode<T>, NodePriority> queue = new(symbolFreqs
            .Select(kvp => KeyValuePair.Create(kvp.Key, Math.Max(1, kvp.Value)))
            .Select((kvp, i) => (new HuffmanNode<T>(kvp.Key, kvp.Value, null, null), new NodePriority(kvp.Value, i))));

        int i = queue.Count;
        while (queue.Count > 1)
        {
            HuffmanNode<T> left = queue.Dequeue();
            HuffmanNode<T> right = queue.Dequeue();
            HuffmanNode<T> parent = new(default, left.Frequency + right.Frequency, left, right);
            NodePriority priority = new(left.Frequency + right.Frequency, i++);
            queue.Enqueue(parent, priority);
        }

        return queue.Dequeue();
    }

    /// <inheritdoc />
    public override string ToString() => Symbol is { } symbol
        ? $"{symbol} ({Frequency})"
        : $"<node> ({Frequency})";
}
