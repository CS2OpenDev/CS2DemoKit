#region

using CS2DemoKit.Analysis.Abstractions;
using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Building;

/// <summary>
///     A frozen, per-fire-frame snapshot of every per-player provider's PRE-FRAME value, built for the
///     Entity-read edge breakpoint substrate (<c>VictimSlot.entity.pawn.health &lt; 20</c>).
///     <para>
///         <b>Why pre-frame:</b> an entity read at a <c>player_death</c> frame sees the victim's pawn
///         already dead (HP 0, filtered out by the providers), so the only meaningful value is the state
///         <em>entering</em> the frame. <see cref="Build" /> positions the layer with
///         <see cref="EntityStateLayer.SeekBeforeFrame" /> — the frame-accurate analogue of the scanner's
///         pre-frame capture — before reading each provider's slots.
///     </para>
///     <para>
///         <b>Build cost &amp; threading:</b> one forward entity replay over the sorted-distinct union of
///         fire frames, capturing <em>all</em> live slots for <em>all</em> providers at each frame (so a
///         condition edit or a different provider re-filters with no replay). The build owns a private
///         <see cref="EntityStateLayer" /> and returns an immutable instance — safe to build off the UI
///         thread and hand back.
///     </para>
/// </summary>
public sealed class EntityValueCache
{
    // (frameIndex, provider name, slot) → boxed value. Frozen after Build; absent key = no value at that
    // frame for that slot (pre-spawn / disconnected / dead pawn filtered by the provider) → null read.
    private readonly Dictionary<(int Frame, string Provider, int Slot), object?> _byFrame;

    private EntityValueCache(Dictionary<(int Frame, string Provider, int Slot), object?> byFrame) =>
        _byFrame = byFrame;

    /// <summary>
    ///     An <see cref="IEntityValueAt" /> reading the cached PRE-FRAME values at
    ///     <paramref name="frameIndex" /> — pass to the compiled edge predicate for a fire in that frame.
    ///     A frame the cache wasn't built for reads as all-null (every provider/slot absent).
    /// </summary>
    public IEntityValueAt At(int frameIndex) => new FrameView(_byFrame, frameIndex);

    /// <summary>
    ///     Builds the cache in ONE forward entity replay over <paramref name="fireFrames" /> (sorted and
    ///     de-duplicated internally; out-of-range indices dropped). At each frame the layer is positioned
    ///     PRE-frame, then every provider's <see cref="IPerPlayerEntityValueProvider.CaptureAllSlots" />
    ///     records each live slot's value. Pure CPU over a fresh layer — call from a worker thread.
    /// </summary>
    public static EntityValueCache Build(
        IReadOnlyList<DemoFrame> frames,
        IReadOnlyList<int> fireFrames,
        IEnumerable<IPerPlayerEntityValueProvider> providers,
        CancellationToken cancellationToken = default)
    {
        IPerPlayerEntityValueProvider[] providerList = providers as IPerPlayerEntityValueProvider[]
                                                       ?? providers.ToArray();
        Dictionary<(int, string, int), object?> byFrame = new();

        int[] sorted = fireFrames
            .Where(fi => fi >= 0 && fi < frames.Count)
            .Distinct()
            .OrderBy(fi => fi)
            .ToArray();

        EntityStateLayer layer = new(frames);
        foreach (int fi in sorted)
        {
            // The replay is the whole ~all-match cost; per-fire-frame checks bound cancel latency
            // to one seek so a superseded build stops burning CPU instead of running to completion.
            cancellationToken.ThrowIfCancellationRequested();
            layer.SeekBeforeFrame(fi);
            foreach (IPerPlayerEntityValueProvider provider in providerList)
            {
                string name = provider.Name;
                provider.CaptureAllSlots(layer, (slot, value) => byFrame[(fi, name, slot)] = value);
            }
        }

        return new EntityValueCache(byFrame);
    }

    // A cheap positioned view — the per-fire accessor handed to the predicate. Stateless (closes over the
    // frozen dict + a frame), so concurrent positions never alias and the cache stays safe to share.
    private sealed class FrameView(Dictionary<(int Frame, string Provider, int Slot), object?> byFrame, int frame)
        : IEntityValueAt
    {
        public object? GetValue(string providerName, int slot) =>
            byFrame.GetValueOrDefault((frame, providerName, slot));
    }
}
