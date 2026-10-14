namespace CS2DemoKit.Analysis.Observation;

/// <summary>
///     A host's per-frame hook into an analysis run. Set through
///     <see cref="AnalysisOptions.FrameObserver" />, it is called once per frame on the evaluation
///     thread, after the frame's messages and synthesized events have dispatched and after the
///     per-pawn fold for that frame, with a <see cref="FrameView" /> over the folded state. It runs
///     at the engine's default parallelism: the digest workers decode ahead, the view reads what
///     they folded, and nothing here touches a tracker.
///     <para>
///         What the view carries is what the run declares: <see cref="AnalysisOptions.ObservedProviders" />
///         for the per-player columns and <see cref="AnalysisOptions.ObservedProjectileClasses" />
///         for the projectile rows. An observer that throws aborts the run, as a throwing edge does;
///         nothing catches it.
///     </para>
/// </summary>
public interface IFrameObserver
{
    /// <summary>One frame, in read order, with the entity state as of the end of that frame.</summary>
    void OnFrame(FrameView frame);

    /// <summary>After the last frame. Not called when the run throws or is cancelled.</summary>
    void Finish();
}
