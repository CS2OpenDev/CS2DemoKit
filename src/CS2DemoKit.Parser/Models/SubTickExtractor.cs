namespace CS2DemoKit.Parser.Models;

/// <summary>
///     Extracts sub-tick input events from a sequence of demo frames by decoding
///     <c>CSVCMsg_UserCommands</c> inner messages and their <c>CSubtickMoveStep</c> entries.
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

    /// <summary>Extract.</summary>
    public static List<SubTickEvent> Extract(IEnumerable<DemoFrame> frames)
    {
        List<SubTickEvent> result = new();

        foreach (DemoFrame frame in frames)
        {
            foreach (CSVCMsg_UserCommands userCmds in UserCommandsIn(frame))
            {
                foreach (CMsgServerUserCmd? serverCmd in userCmds.Commands)
                {
                    try
                    {
                        CSGOUserCmdPB? cmd = CSGOUserCmdPB.Parser.ParseFrom(serverCmd.Data);
                        if (cmd?.Base is null)
                        {
                            continue;
                        }

                        foreach (CSubtickMoveStep? step in cmd.Base.SubtickMoves)
                        {
                            ulong btn = step.Button;
                            string eventType = ClassifyButton(btn);
                            string desc = BuildDescription(btn, step);

                            result.Add(new SubTickEvent
                            {
                                When = step.When,
                                EventType = eventType,
                                Description = desc,
                                PlayerSlot = serverCmd.PlayerSlot,
                                CmdNumber = serverCmd.CmdNumber
                            });
                        }
                    }
                    catch
                    {
                        // Silent skip on decode failure
                    }
                }
            }
        }

        result.Sort((a, b) => a.When.CompareTo(b.When));
        return result;
    }

    /// <summary>
    ///     This frame's subtick messages. Parse-time payloads live in the frame's arena slab and are
    ///     decoded here, on the one path that reads them. A frame built by hand (tests, synthetic
    ///     input) carries them in <see cref="DemoFrame.InnerMessages" /> instead, so both are read.
    /// </summary>
    private static IEnumerable<CSVCMsg_UserCommands> UserCommandsIn(DemoFrame frame)
    {
        if (frame.SubtickSlab is { } slab)
        {
            int offset = frame.SubtickOffset;
            for (int i = 0; i < frame.SubtickCount; i++)
            {
                ReadOnlySpan<byte> payload = SubtickArena.Read(slab, ref offset);
                CSVCMsg_UserCommands? parsed = TryParse(payload);
                if (parsed is not null)
                {
                    yield return parsed;
                }
            }
        }

        foreach (NetMessage msg in frame.InnerMessages)
        {
            CSVCMsg_UserCommands? direct = msg.Payload switch
            {
                CSVCMsg_UserCommands typed => typed,
                DeferredMessage deferred => deferred.TryMaterialize<CSVCMsg_UserCommands>(),
                _ => null
            };
            if (direct is not null)
            {
                yield return direct;
            }
        }
    }

    /// <summary>Decodes one arena payload, returning null rather than throwing on a bad record.</summary>
    private static CSVCMsg_UserCommands? TryParse(ReadOnlySpan<byte> payload)
    {
        try
        {
            return CSVCMsg_UserCommands.Parser.ParseFrom(payload);
        }
        catch
        {
            return null;
        }
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
