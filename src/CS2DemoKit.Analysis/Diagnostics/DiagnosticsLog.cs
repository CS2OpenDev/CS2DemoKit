#region

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Diagnostics;

/// <summary>
///     The ambient logger seam for the first-party diagnostics pillar — the logging counterpart of
///     <see cref="CS2DemoKit.Parser.Profiling" />'s runtime switch. Libraries below the UI (this
///     Analysis assembly, and any consumer that references it) get an <see cref="ILogger" /> from
///     here instead of taking a DI dependency, so coarse lifecycle/warning logs can be emitted with
///     no constructor plumbing.
///     <para>
///         <b>Default:</b> <see cref="NullLoggerFactory" /> — every emit site is a single predicted
///         branch (the source-generated <c>[LoggerMessage]</c> methods short-circuit on
///         <see cref="ILogger.IsEnabled" />, which a null logger answers <c>false</c>), so a host that
///         never wires a factory pays nothing. The App assigns a real factory (feeding the Diagnostics
///         tab + the rolling file) once at startup, <b>before</b> any analysis runs — analysis only
///         executes on demo load, well after that, so loggers resolved lazily observe the real factory.
///     </para>
/// </summary>
public static class DiagnosticsLog
{
    private static volatile ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>
    ///     The process-wide factory. Assign once at startup; assigning <c>null</c> reverts to the
    ///     no-op <see cref="NullLoggerFactory" />. Reads are a volatile field load.
    /// </summary>
    public static ILoggerFactory LoggerFactory
    {
        get => _factory;
        set => _factory = value ?? NullLoggerFactory.Instance;
    }

    /// <summary>Creates a category-named logger from the ambient factory (a null logger by default).</summary>
    public static ILogger CreateLogger(string category) => _factory.CreateLogger(category);

    /// <summary>Creates a <typeparamref name="T" />-categorized logger from the ambient factory.</summary>
    public static ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();
}
