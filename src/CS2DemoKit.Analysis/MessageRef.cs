#region

using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis;

/// <summary>
///     One dispatched message's position in an evaluation: the frame it arrived in, that frame's
///     tick, and its ordinal among the messages the evaluator dispatched for that frame (synthesized
///     entity-change messages first, then the frame's decoded messages). Holds the message, not the
///     frame, so a snapshot row never pins a frame the source has let go of; a caller with random
///     access indexes its own frame list by <see cref="FrameIndex" />.
/// </summary>
public readonly record struct MessageRef(int FrameIndex, int Tick, int Ordinal, NetMessage Message);
