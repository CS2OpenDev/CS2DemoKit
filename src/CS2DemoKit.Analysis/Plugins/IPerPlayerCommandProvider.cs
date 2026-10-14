namespace CS2DemoKit.Analysis.Plugins;

/// <summary>
///     Reads one value off a player's latest user command, the sibling of
///     <see cref="IPerPlayerEntityValueProvider" /> for input rather than entity state. It has no
///     digest column: the evaluator rebuilds the commands on its own thread with a
///     <c>UserCmdReconstructor</c>, and <c>EntityChangeScanner.GetCommandValue</c> serves reads from it.
///     <para>
///         A read sees the slot's latest command as of the current frame, its own commands included.
///         That is one frame later than an entity read, which sees the frame before.
///     </para>
/// </summary>
public interface IPerPlayerCommandProvider
{
    /// <summary>The catalog name, <c>usercmd.&lt;member&gt;</c>; rules read it as <c>player.cmd.&lt;member&gt;</c>.</summary>
    string Name { get; }

    /// <summary>Runtime type of the value.</summary>
    Type ValueType { get; }

    /// <summary>The value off <paramref name="command" />, boxed in <see cref="ValueType" />, or null when the command lacks it.</summary>
    object? Read(CSGOUserCmdPB command);
}

/// <summary>The built-in <see cref="IPerPlayerCommandProvider" /> set.</summary>
public static class UserCommandProviders
{
    /// <summary>The prefix every command provider name carries.</summary>
    public const string Prefix = "usercmd.";

    /// <summary><c>player.cmd.buttons</c>: <c>base.buttons_pb.buttonstate1</c>, the held-button mask.</summary>
    public static IPerPlayerCommandProvider Buttons { get; } = new Provider("usercmd.buttons", typeof(long),
        c => c.Base?.ButtonsPb is { } b ? unchecked((long)b.Buttonstate1) : null);

    /// <summary><c>player.cmd.forward</c>: <c>base.forwardmove</c>.</summary>
    public static IPerPlayerCommandProvider Forward { get; } = new Provider("usercmd.forward", typeof(float),
        c => c.Base is { } b ? b.Forwardmove : null);

    /// <summary><c>player.cmd.left</c>: <c>base.leftmove</c>.</summary>
    public static IPerPlayerCommandProvider Left { get; } = new Provider("usercmd.left", typeof(float),
        c => c.Base is { } b ? b.Leftmove : null);

    /// <summary><c>player.cmd.view_pitch</c>: <c>base.viewangles.x</c>, in degrees.</summary>
    public static IPerPlayerCommandProvider ViewPitch { get; } = new Provider("usercmd.view_pitch", typeof(float),
        c => c.Base?.Viewangles is { } v ? v.X : null);

    /// <summary><c>player.cmd.view_yaw</c>: <c>base.viewangles.y</c>, in degrees.</summary>
    public static IPerPlayerCommandProvider ViewYaw { get; } = new Provider("usercmd.view_yaw", typeof(float),
        c => c.Base?.Viewangles is { } v ? v.Y : null);

    /// <summary><c>player.cmd.tick</c>: <c>base.client_tick</c>, the client tick the command was built on.</summary>
    public static IPerPlayerCommandProvider Tick { get; } = new Provider("usercmd.tick", typeof(int),
        c => c.Base is { } b ? b.ClientTick : null);

    /// <summary>Every built-in command provider, in catalog order.</summary>
    public static IReadOnlyList<IPerPlayerCommandProvider> All { get; } =
        [Buttons, Forward, Left, ViewPitch, ViewYaw, Tick];

    /// <summary>The provider named <paramref name="name" />, or null.</summary>
    public static IPerPlayerCommandProvider? Get(string name)
    {
        foreach (IPerPlayerCommandProvider provider in All)
        {
            if (string.Equals(provider.Name, name, StringComparison.Ordinal))
            {
                return provider;
            }
        }

        return null;
    }

    private sealed class Provider(string name, Type valueType, Func<CSGOUserCmdPB, object?> read)
        : IPerPlayerCommandProvider
    {
        public string Name => name;

        public Type ValueType => valueType;

        public object? Read(CSGOUserCmdPB command)
        {
            ArgumentNullException.ThrowIfNull(command);
            return read(command);
        }
    }
}
