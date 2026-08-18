namespace CS2DemoKit.Analysis.Rules.Ast;

/// <summary>Binary operators of the spec §2 grammar, in precedence-family groups.</summary>
public enum BinaryOperator
{
    /// <summary>Unset. Never produced by the parser.</summary>
    None = 0,

    /// <summary><c>||</c> / <c>or</c>.</summary>
    Or,

    /// <summary><c>&amp;&amp;</c> / <c>and</c>.</summary>
    And,

    /// <summary><c>==</c>.</summary>
    Equal,

    /// <summary><c>!=</c>.</summary>
    NotEqual,

    /// <summary><c>&gt;</c>.</summary>
    Greater,

    /// <summary><c>&gt;=</c>.</summary>
    GreaterOrEqual,

    /// <summary><c>&lt;</c>.</summary>
    Less,

    /// <summary><c>&lt;=</c>.</summary>
    LessOrEqual,

    /// <summary><c>+</c>.</summary>
    Add,

    /// <summary><c>-</c>.</summary>
    Subtract,

    /// <summary><c>*</c>.</summary>
    Multiply,

    /// <summary><c>/</c>.</summary>
    Divide,

    /// <summary><c>%</c>.</summary>
    Modulo,

    /// <summary><c>in</c> — scalar on the left, list reference or constant list literal on the right.</summary>
    In
}

/// <summary>Unary operators of the spec §2 grammar.</summary>
public enum UnaryOperator
{
    /// <summary>Unset. Never produced by the parser.</summary>
    None = 0,

    /// <summary><c>!</c> / <c>not</c>.</summary>
    Not,

    /// <summary>Unary minus. Applied directly to a numeric literal it folds into a negative literal instead.</summary>
    Negate
}

/// <summary>The closed function set of spec §2. Additions are a minor-version spec change; removals never happen.</summary>
public enum RuleFunction
{
    /// <summary>Unset. Never produced by the parser.</summary>
    None = 0,

    /// <summary><c>min(int|float|duration, same) → same</c>; mixed int/float → float.</summary>
    Min,

    /// <summary><c>max(int|float|duration, same) → same</c>; mixed int/float → float.</summary>
    Max,

    /// <summary><c>abs(int|float|duration) → same</c>.</summary>
    Abs,

    /// <summary><c>contains(string, string) → bool</c>; ordinal, case-sensitive.</summary>
    Contains,

    /// <summary><c>startswith(string, string) → bool</c>; ordinal, case-sensitive.</summary>
    StartsWith,

    /// <summary><c>floor(int|float|duration) → same</c>; largest integral value ≤ the argument.</summary>
    Floor
}

/// <summary>Operator/function spellings shared by the canonical writer and diagnostics.</summary>
internal static class OperatorText
{
    /// <summary>Canonical serialization tag for a binary operator.</summary>
    /// <param name="op">The operator.</param>
    /// <returns>The tag, e.g. <c>gt</c> for <see cref="BinaryOperator.Greater" />.</returns>
    internal static string CanonicalTag(BinaryOperator op) =>
        op switch
        {
            BinaryOperator.Or => "or",
            BinaryOperator.And => "and",
            BinaryOperator.Equal => "eq",
            BinaryOperator.NotEqual => "ne",
            BinaryOperator.Greater => "gt",
            BinaryOperator.GreaterOrEqual => "ge",
            BinaryOperator.Less => "lt",
            BinaryOperator.LessOrEqual => "le",
            BinaryOperator.Add => "add",
            BinaryOperator.Subtract => "sub",
            BinaryOperator.Multiply => "mul",
            BinaryOperator.Divide => "div",
            BinaryOperator.Modulo => "mod",
            BinaryOperator.In => "in",
            _ => "none"
        };

    /// <summary>The user-facing spelling of a binary operator for diagnostics.</summary>
    /// <param name="op">The operator.</param>
    /// <returns>The source spelling, e.g. <c>&gt;</c>.</returns>
    internal static string Display(BinaryOperator op) =>
        op switch
        {
            BinaryOperator.Or => "or",
            BinaryOperator.And => "and",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterOrEqual => ">=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessOrEqual => "<=",
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.In => "in",
            _ => "?"
        };

    /// <summary>The lowercase source name of a closed-set function.</summary>
    /// <param name="function">The function.</param>
    /// <returns>The name, e.g. <c>startswith</c>.</returns>
    internal static string Name(RuleFunction function) =>
        function switch
        {
            RuleFunction.Min => "min",
            RuleFunction.Max => "max",
            RuleFunction.Abs => "abs",
            RuleFunction.Contains => "contains",
            RuleFunction.StartsWith => "startswith",
            RuleFunction.Floor => "floor",
            _ => "none"
        };
}
