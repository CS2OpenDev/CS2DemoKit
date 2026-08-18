namespace CS2DemoKit.Parser.Entities;

/// <summary>
///     One entity class from the flattened serializer schema.
/// </summary>
public sealed class RuntimeSerializer
{
    internal RuntimeSerializer(string name, int version, RuntimeField[] fields)
    {
        Name = name;
        Version = version;
        Fields = fields;
    }

    /// <summary>Fields.</summary>
    public RuntimeField[] Fields { get; }

    /// <summary>Name.</summary>
    public string Name { get; }

    /// <summary>Version.</summary>
    public int Version { get; }
}
