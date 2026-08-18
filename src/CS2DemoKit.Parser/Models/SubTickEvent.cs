namespace CS2DemoKit.Parser.Models;

/// <summary>Sub tick event.</summary>
public sealed class SubTickEvent
{
    /// <summary>Cmd number.</summary>
    public int CmdNumber { get; init; }

    /// <summary>Description.</summary>
    public string Description { get; init; } = "";

    /// <summary>Event type.</summary>
    public string EventType { get; init; } = "";

    /// <summary>Player slot.</summary>
    public int PlayerSlot { get; init; }

    /// <summary>When.</summary>
    public float When { get; init; } // 0.0–1.0 position within the tick
}
