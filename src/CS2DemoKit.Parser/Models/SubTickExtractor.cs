#region

using CS2DemoKit.Parser.EntityTracking;

#endregion

namespace CS2DemoKit.Parser.Models;

/// <summary>
///     Extracts sub-tick input events from a sequence of demo frames: one event per
///     <c>CSubtickMoveStep</c> of every player command, sorted by <see cref="SubTickEvent.When" />.
///     <para>
///         Commands are read through a <see cref="UserCmdReconstructor" />, so the ones the server sent
///         as <c>delta_data</c> (about 99.8% of them since build 10896) are rebuilt rather than
///         skipped. Snapshot commands inside <c>DEM_FullPacket</c> frames prime the reconstructor and
///         produce no events, so they are not counted twice. Feed frames from frame 0 or from a
///         <c>DEM_FullPacket</c>; the decode plan must include <see cref="MessageCategories.UserCmds" />.
///     </para>
/// </summary>
public static class SubTickExtractor
{
    // CS2 button bit flags
    private const ulong InAttack = 1ul << 0;
    private const ulong InAttack2 = 1ul << 11;
    private const ulong InBack = 1ul << 4;
    private const ulong InDuck = 1ul << 2;
    private const ulong InForward = 1ul << 3;
    private const ulong InJump = 1ul << 1;
    private const ulong InLeft = 1ul << 7;
    private const ulong InReload = 1ul << 12;
    private const ulong InRight = 1ul << 8;
    private const ulong InUse = 1ul << 5;

    /// <summary>Extracts every sub-tick event in <paramref name="frames" /> with a fresh reconstructor.</summary>
    public static List<SubTickEvent> Extract(IEnumerable<DemoFrame> frames) =>
        Extract(frames, new UserCmdReconstructor());

    /// <summary>
    ///     Extracts every sub-tick event in <paramref name="frames" /> through
    ///     <paramref name="reconstructor" />, whose <see cref="UserCmdReconstructor.Stats" /> then say
    ///     how many commands were rebuilt and how many could not be. The reconstructor keeps its state,
    ///     so a caller can continue it across calls.
    /// </summary>
    public static List<SubTickEvent> Extract(IEnumerable<DemoFrame> frames, UserCmdReconstructor reconstructor)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(reconstructor);
        List<SubTickEvent> result = new();

        foreach (DemoFrame frame in frames)
        {
            foreach (ReconstructedUserCmd cmd in reconstructor.AdvanceOneFrame(frame))
            {
                if (cmd.Command.Base is not { } baseCmd)
                {
                    continue;
                }

                foreach (CSubtickMoveStep step in baseCmd.SubtickMoves)
                {
                    ulong btn = step.Button;
                    result.Add(new SubTickEvent
                    {
                        When = step.When,
                        EventType = ClassifyButton(btn),
                        Description = BuildDescription(btn, step),
                        PlayerSlot = cmd.PlayerSlot,
                        CmdNumber = cmd.CmdNumber
                    });
                }
            }
        }

        result.Sort((a, b) => a.When.CompareTo(b.When));
        return result;
    }

    private static string BuildDescription(ulong btn, CSubtickMoveStep step)
    {
        List<string> parts = new();

        if ((btn & InForward) != 0)
        {
            parts.Add("fwd");
        }

        if ((btn & InBack) != 0)
        {
            parts.Add("back");
        }

        if ((btn & InLeft) != 0)
        {
            parts.Add("left");
        }

        if ((btn & InRight) != 0)
        {
            parts.Add("right");
        }

        if ((btn & InAttack) != 0)
        {
            parts.Add("primary");
        }

        if ((btn & InAttack2) != 0)
        {
            parts.Add("secondary");
        }

        if ((btn & InJump) != 0)
        {
            parts.Add("IN_JUMP");
        }

        if ((btn & InDuck) != 0)
        {
            parts.Add("IN_DUCK");
        }

        if ((btn & InReload) != 0)
        {
            parts.Add("IN_RELOAD");
        }

        if ((btn & InUse) != 0)
        {
            parts.Add("IN_USE");
        }

        bool pressed = step is { HasPressed: true, Pressed: true };
        string action = pressed ? "+" : "-";

        string desc = parts.Count > 0 ? $"{action}[{string.Join("+", parts)}]" : $"btn=0x{btn:X}";

        if (step.HasAnalogForwardDelta && step.AnalogForwardDelta != 0f)
        {
            desc += $" fwd={step.AnalogForwardDelta:F2}";
        }

        if (step.HasAnalogLeftDelta && step.AnalogLeftDelta != 0f)
        {
            desc += $" left={step.AnalogLeftDelta:F2}";
        }

        return desc;
    }

    private static string ClassifyButton(ulong btn)
    {
        if ((btn & InAttack) != 0)
        {
            return "Attack";
        }

        if ((btn & InAttack2) != 0)
        {
            return "Attack2";
        }

        if ((btn & InJump) != 0)
        {
            return "Jump";
        }

        if ((btn & InDuck) != 0)
        {
            return "Duck";
        }

        if ((btn & InReload) != 0)
        {
            return "Reload";
        }

        if ((btn & InUse) != 0)
        {
            return "Use";
        }

        if ((btn & (InForward | InBack | InLeft | InRight)) != 0)
        {
            return "Move";
        }

        return $"Button(0x{btn:X})";
    }
}
