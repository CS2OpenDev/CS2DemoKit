#region

using System.Runtime.CompilerServices;
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
    ///     Resolves a declared provider name once. Reads through the handle skip the name lookup,
    ///     which is what a hot adapter wants: at seven columns and 64 slots a frame, the lookup
    ///     alone was most of the read. Callable outside <c>OnFrame</c>; the handle is valid for this
    ///     view, which is one run.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not among <see cref="AnalysisOptions.ObservedProviders" />.</exception>
    public ObservedProvider Provider(string providerName)
    {
        int column = Column(providerName);
        return new ObservedProvider(this, column, _scanner!.ColumnLayout.Kinds[column], _scanner.ColumnLayout.Names[column]);
    }

    /// <summary>
    ///     A declared per-player provider's value for a slot, boxed in its declared type, or null
    ///     when the slot has no value yet. The typed reads below are allocation-free.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not among <see cref="AnalysisOptions.ObservedProviders" />.</exception>
    public object? Read(string providerName, int playerSlot) => Read(Provider(providerName), playerSlot);

    /// <summary>An int or bool column's value (a bool reads as 0 or 1). False when the slot has no value yet.</summary>
    /// <exception cref="ArgumentException">The name is not declared, or the column is not an int or bool.</exception>
    public bool TryReadInt(string providerName, int playerSlot, out int value) => TryReadInt(Provider(providerName), playerSlot, out value);

    /// <summary>A float column's value. False when the slot has no value yet.</summary>
    /// <exception cref="ArgumentException">The name is not declared, or the column is not a float.</exception>
    public bool TryReadFloat(string providerName, int playerSlot, out float value) => TryReadFloat(Provider(providerName), playerSlot, out value);

    /// <summary>A string column's value. False when the slot has no value yet.</summary>
    /// <exception cref="ArgumentException">The name is not declared, or the column is not a string.</exception>
    public bool TryReadString(string providerName, int playerSlot, out string? value) => TryReadString(Provider(providerName), playerSlot, out value);

    /// <summary><see cref="Read(string, int)" /> through a resolved handle.</summary>
    public object? Read(ObservedProvider provider, int playerSlot) =>
        Open()._scanner!.Snapshot.GetBoxed(Own(provider).Column, playerSlot);

    /// <summary><see cref="TryReadInt(string, int, out int)" /> through a resolved handle.</summary>
    public bool TryReadInt(ObservedProvider provider, int playerSlot, out int value)
    {
        Own(provider);
        if (provider.Kind is not (PawnCellKind.Int or PawnCellKind.Bool))
        {
            throw Kind(provider.Name, provider.Kind, "int");
        }

        return Open()._scanner!.Snapshot.TryGetInt(provider.Column, playerSlot, out value);
    }

    /// <summary><see cref="TryReadFloat(string, int, out float)" /> through a resolved handle.</summary>
    public bool TryReadFloat(ObservedProvider provider, int playerSlot, out float value)
    {
        Own(provider);
        if (provider.Kind != PawnCellKind.Float)
        {
            throw Kind(provider.Name, provider.Kind, "float");
        }

        return Open()._scanner!.Snapshot.TryGetFloat(provider.Column, playerSlot, out value);
    }

    /// <summary><see cref="TryReadString(string, int, out string?)" /> through a resolved handle.</summary>
    public bool TryReadString(ObservedProvider provider, int playerSlot, out string? value)
    {
        Own(provider);
        if (provider.Kind != PawnCellKind.String)
        {
            throw Kind(provider.Name, provider.Kind, "string");
        }

        return Open()._scanner!.Snapshot.TryGetString(provider.Column, playerSlot, out value);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private FrameView Open() =>
        _open
            ? this
            : throw new InvalidOperationException(
                "FrameView is readable only inside IFrameObserver.OnFrame; copy what you keep before returning");

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ObservedProvider Own(in ObservedProvider provider) =>
        ReferenceEquals(provider.Owner, this)
            ? provider
            : throw new ArgumentException(
                provider.Owner is null
                    ? "the provider handle is default; resolve one with FrameView.Provider"
                    : $"the handle for '{provider.Name}' belongs to another run's view",
                nameof(provider));

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

/// <summary>
///     A declared provider resolved against one run's <see cref="FrameView" /> through
///     <see cref="FrameView.Provider" />. Reads through it skip the name lookup. Valid only on the
///     view that made it; a view of another run rejects it.
/// </summary>
public readonly struct ObservedProvider
{
    internal ObservedProvider(FrameView owner, int column, PawnCellKind kind, string name)
    {
        Owner = owner;
        Column = column;
        Kind = kind;
        Name = name;
    }

    internal FrameView? Owner { get; }
    internal int Column { get; }

    /// <summary>The column's storage kind, which picks the typed read that applies.</summary>
    public PawnCellKind Kind { get; }

    /// <summary>The provider's catalog name as the layout spells it.</summary>
    public string Name { get; }
}
