#region

using CS2DemoKit.Analysis.Plugins;
using CS2DemoKit.Parser;

#endregion

namespace CS2DemoKit.Analysis.Observation;

/// <summary>
///     A read-only window over one frame's folded entity state, handed to
///     <see cref="IFrameObserver.OnFrame" />. One instance per run, rewritten before each call;
///     every read but <see cref="Generation" /> throws outside <c>OnFrame</c>, so a reference an
///     adapter keeps fails loudly instead of reading a later frame.
///     <para>
///         Pawn reads are the declared providers' values as of the end of this frame, the same
///         cells the next frame's first edge reads through the pre-frame snapshot. Projectile rows
///         are the declared classes' lifecycle on this frame. Both are empty when nothing was
///         declared.
///     </para>
/// </summary>
public sealed class FrameView
{
    private readonly EntityChangeScanner? _scanner;
    private readonly Dictionary<string, int> _columns;
    private readonly string[] _declared;
    private DemoFrame? _frame;
    private int _frameIndex;
    private bool _open;

    internal FrameView(EntityChangeScanner? scanner, IReadOnlyList<string> declaredProviders)
    {
        _scanner = scanner;
        _declared = [.. declaredProviders];
        _columns = new Dictionary<string, int>(_declared.Length, StringComparer.OrdinalIgnoreCase);
        string[] names = scanner?.ColumnLayout.Names ?? [];
        foreach (string name in _declared)
        {
            int column = Array.FindIndex(names, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (column < 0)
            {
                throw new InvalidOperationException(
                    $"observed provider '{name}' is not a column of this run's scanner; the build did not activate it");
            }

            _columns[name] = column;
        }
    }

    /// <summary>Increments per frame. An adapter that holds the view checks it before a late read.</summary>
    public long Generation { get; private set; }

    /// <summary>The frame just dispatched.</summary>
    public DemoFrame Frame => Open()._frame!;

    /// <summary>The frame's index in the run's read order, from zero.</summary>
    public int FrameIndex => Open()._frameIndex;

    /// <summary>The frame clock, the tick every synthesized event stamps (<c>Frame.GameTick ?? Frame.ServerTick</c>).</summary>
    public int Tick
    {
        get
        {
            DemoFrame frame = Frame;
            return frame.GameTick ?? frame.ServerTick;
        }
    }

    /// <summary>Every live projectile of an observed class, ascending entity index, throwers held from first resolution.</summary>
    public ReadOnlySpan<ProjectileRow> Projectiles => Open()._scanner is { } s ? s.Projectiles.Live : default;

    /// <summary>Projectiles whose (index, serial) appeared this frame.</summary>
    public ReadOnlySpan<ProjectileRow> Created => Open()._scanner is { } s ? s.Projectiles.Created : default;

    /// <summary>Projectiles that were live last frame and are gone this frame, each with its last-seen row.</summary>
    public ReadOnlySpan<ProjectileRow> Removed => Open()._scanner is { } s ? s.Projectiles.Removed : default;

    /// <summary>
    ///     A declared per-player provider's value for a slot, boxed in its declared type, or null
    ///     when the slot has no value yet. The typed reads below are allocation-free.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not among <see cref="AnalysisOptions.ObservedProviders" />.</exception>
    public object? Read(string providerName, int playerSlot) =>
        Open()._scanner!.Snapshot.GetBoxed(Column(providerName), playerSlot);

    /// <summary>An int or bool column's value (a bool reads as 0 or 1). False when the slot has no value yet.</summary>
    /// <exception cref="ArgumentException">The name is not declared, or the column is not an int or bool.</exception>
    public bool TryReadInt(string providerName, int playerSlot, out int value)
    {
        int column = Column(providerName);
        Open();
        PawnCellKind kind = _scanner!.ColumnLayout.Kinds[column];
        if (kind is not (PawnCellKind.Int or PawnCellKind.Bool))
        {
            throw Kind(providerName, kind, "int");
        }

        return _scanner.Snapshot.TryGetInt(column, playerSlot, out value);
    }

    /// <summary>A float column's value. False when the slot has no value yet.</summary>
    /// <exception cref="ArgumentException">The name is not declared, or the column is not a float.</exception>
    public bool TryReadFloat(string providerName, int playerSlot, out float value)
    {
        int column = Column(providerName);
        Open();
        PawnCellKind kind = _scanner!.ColumnLayout.Kinds[column];
        if (kind != PawnCellKind.Float)
        {
            throw Kind(providerName, kind, "float");
        }

        return _scanner.Snapshot.TryGetFloat(column, playerSlot, out value);
    }

    /// <summary>A string column's value. False when the slot has no value yet.</summary>
    /// <exception cref="ArgumentException">The name is not declared, or the column is not a string.</exception>
    public bool TryReadString(string providerName, int playerSlot, out string? value)
    {
        int column = Column(providerName);
        Open();
        PawnCellKind kind = _scanner!.ColumnLayout.Kinds[column];
        if (kind != PawnCellKind.String)
        {
            throw Kind(providerName, kind, "string");
        }

        return _scanner.Snapshot.TryGetString(column, playerSlot, out value);
    }

    /// <summary>The slot's controller team as last seen (2 = T, 3 = CT), or -1.</summary>
    public int Team(int playerSlot) =>
        Open()._scanner is { } scanner && scanner.TryGetTeam(playerSlot, out int team) ? team : -1;

    internal void Publish(DemoFrame frame, int frameIndex)
    {
        _frame = frame;
        _frameIndex = frameIndex;
        Generation++;
        _open = true;
    }

    internal void Close()
    {
        _open = false;
        _frame = null;
    }

    private FrameView Open() =>
        _open
            ? this
            : throw new InvalidOperationException(
                "FrameView is readable only inside IFrameObserver.OnFrame; copy what you keep before returning");

    private int Column(string providerName) =>
        _columns.TryGetValue(providerName, out int column)
            ? column
            : throw new ArgumentException(
                $"provider '{providerName}' is not declared in AnalysisOptions.ObservedProviders "
                + $"(declared: {(_declared.Length == 0 ? "none" : string.Join(", ", _declared))})",
                nameof(providerName));

    private static ArgumentException Kind(string providerName, PawnCellKind actual, string wanted) =>
        new($"provider '{providerName}' is a {actual.ToString().ToLowerInvariant()} column, not {wanted}", nameof(providerName));
}
