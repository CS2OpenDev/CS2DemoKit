namespace CS2DemoKit.Analysis.Rules;

/// <summary>
///     Value-or-diagnostics result of a semantic-core stage. User-input errors always travel
///     as <see cref="Diagnostic" /> lists through these results — never as exceptions.
/// </summary>
/// <typeparam name="T">The stage's product (token list, AST root, checked expression, …).</typeparam>
public sealed class LanguageResult<T>
{
    internal LanguageResult(bool success, T? value, IReadOnlyList<Diagnostic> diagnostics)
    {
        Success = success;
        Value = value;
        Diagnostics = diagnostics;
    }

    /// <summary>True when the stage produced a value; <see cref="Value" /> is then non-null.</summary>
    public bool Success { get; }

    /// <summary>The stage's product, or <c>null</c> when <see cref="Success" /> is false.</summary>
    public T? Value { get; }

    /// <summary>All diagnostics the stage reported. Non-empty whenever <see cref="Success" /> is false.</summary>
    public IReadOnlyList<Diagnostic> Diagnostics { get; }

    /// <summary>
    ///     Returns <see cref="Value" /> or throws when the result failed. For call sites that
    ///     have already established success (e.g. pipelines over known-good input); the throw
    ///     is programmer misuse, not error reporting.
    /// </summary>
    /// <returns>The non-null value.</returns>
    /// <exception cref="InvalidOperationException">The result carries diagnostics instead of a value.</exception>
    public T Require() =>
        Success && Value is not null
            ? Value
            : throw new InvalidOperationException(
                $"expression stage failed: {string.Join(" | ", Diagnostics)}");
}

/// <summary>Factories for <see cref="LanguageResult{T}" />.</summary>
public static class LanguageResult
{
    /// <summary>Creates a successful result.</summary>
    /// <typeparam name="T">The stage's product type.</typeparam>
    /// <param name="value">The produced value.</param>
    /// <returns>A successful result with no diagnostics.</returns>
    public static LanguageResult<T> Ok<T>(T value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new LanguageResult<T>(true, value, []);
    }

    /// <summary>Creates a failed result.</summary>
    /// <typeparam name="T">The stage's product type.</typeparam>
    /// <param name="diagnostics">The problems found; must contain at least one entry.</param>
    /// <returns>A failed result carrying <paramref name="diagnostics" />.</returns>
    public static LanguageResult<T> Fail<T>(IReadOnlyList<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        return diagnostics.Count == 0
            ? throw new ArgumentException("a failed result needs at least one diagnostic", nameof(diagnostics))
            : new LanguageResult<T>(false, default, diagnostics);
    }

    /// <summary>Creates a failed result from a single diagnostic.</summary>
    /// <typeparam name="T">The stage's product type.</typeparam>
    /// <param name="diagnostic">The problem found.</param>
    /// <returns>A failed result carrying the diagnostic.</returns>
    public static LanguageResult<T> Fail<T>(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new LanguageResult<T>(false, default, [diagnostic]);
    }
}
