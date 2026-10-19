namespace CS2DemoKit.Parser.EntityTracking;

/// <summary>
///     Follows the <c>svc_PacketEntities</c> delta chain on the sequential path. Each delta names the
///     server tick it was encoded against (<c>delta_from</c>), and that must be the tick of the last
///     snapshot applied. A GOTV recording that drops a snapshot leaves the next delta pointing at a
///     tick the tracker never saw, and every later delta chains correctly onto that one, so the break
///     latches until a <c>DEM_FullPacket</c> resyncs the state.
/// </summary>
internal sealed class SnapshotChain
{
    // Ticks are widened to long: delta_from is int32 on the wire and server_tick is uint32.
    private long _lastTick = -1;

    /// <summary>Whether a delta named a base the tracker never applied, since the last resync.</summary>
    public bool Broken { get; private set; }

    /// <summary>Records a packet applied on the sequential path.</summary>
    public void Observe(CSVCMsg_PacketEntities msg) =>
        Observe(msg.LegacyIsDelta, msg.HasDeltaFrom, msg.DeltaFrom, msg.HasServerTick, msg.ServerTick);

    /// <summary>Records a packet applied on the sequential path.</summary>
    /// <remarks>An unknown last tick never breaks the chain: the demo carries nothing to check against.</remarks>
    public void Observe(bool isDelta, bool hasDeltaFrom, long deltaFrom, bool hasServerTick, long serverTick)
    {
        if (isDelta && hasDeltaFrom && _lastTick >= 0 && deltaFrom != _lastTick)
        {
            Broken = true;
        }

        _lastTick = hasServerTick ? serverTick : -1;
    }

    /// <summary>Starts a fresh chain at a full snapshot that replaced the entity state.</summary>
    public void Seed(CSVCMsg_PacketEntities msg) => Seed(msg.HasServerTick, msg.ServerTick);

    /// <summary>Starts a fresh chain at a full snapshot that replaced the entity state.</summary>
    public void Seed(bool hasServerTick, long serverTick)
    {
        Broken = false;
        _lastTick = hasServerTick ? serverTick : -1;
    }
}
