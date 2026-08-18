#region

using System.Reflection;
using System.Text;
using CS2DemoKit.Analysis.Abstractions;

#endregion

namespace CS2DemoKit.Analysis.Profiles;

/// <summary>
///     Resolves logical-event references (e.g. <c>$round_end</c>,
///     <c>round_end</c>, <c>RoundEnd</c>) against an active
///     <see cref="DemoSourceProfile" />. The mapping from snake_case logical
///     names to <c>DemoSourceProfile</c> properties is built once at type
///     init via reflection — there is no per-build reflection cost.
/// </summary>
/// <remarks>
///     Logical events are referenced two ways in rule configs:
///     <list type="bullet">
///         <item>
///             <description>Trigger <c>on:</c> strings prefixed with <c>$</c> — e.g. <c>$round_end</c>.</description>
///         </item>
///         <item>
///             <description><c>requires:</c> entries — bare snake_case names, e.g. <c>player_blind</c>.</description>
///         </item>
///     </list>
///     Both forms map through the same reflection table.
/// </remarks>
public sealed class LogicalEventResolver
{
    private static readonly Dictionary<string, Func<DemoSourceProfile, LogicalEventBinding?>> _accessors
        = BuildAccessors();

    /// <param name="profile">The active source profile to resolve logical names against.</param>
    public LogicalEventResolver(DemoSourceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Profile = profile;
    }

    /// <summary>The active profile this resolver was constructed with.</summary>
    public DemoSourceProfile Profile { get; }

    /// <summary>
    ///     True when the logical name maps to a known property on
    ///     <see cref="DemoSourceProfile" /> (regardless of whether the active
    ///     profile binds it).
    /// </summary>
    public static bool IsKnownLogicalName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = name[0] == '$'
            ? name.AsSpan(1)
            : name.AsSpan();
        return _accessors.ContainsKey(trimmed.ToString());
    }

    /// <summary>True when the trigger string starts with <c>$</c>.</summary>
    public static bool IsLogicalReference(string triggerOn) =>
        !string.IsNullOrEmpty(triggerOn) && triggerOn[0] == '$';

    /// <summary>
    ///     Resolves a logical name (with or without leading <c>$</c>) against
    ///     the active profile. Returns the binding, or <c>null</c> if either
    ///     the name is unknown or the profile does not bind it.
    /// </summary>
    public LogicalEventBinding? Resolve(string logicalName)
    {
        if (string.IsNullOrEmpty(logicalName))
        {
            return null;
        }

        ReadOnlySpan<char> name = logicalName[0] == '$'
            ? logicalName.AsSpan(1)
            : logicalName.AsSpan();

        return _accessors.TryGetValue(name.ToString(), out Func<DemoSourceProfile, LogicalEventBinding?>? accessor)
            ? accessor(Profile)
            : null;
    }

    private static Dictionary<string, Func<DemoSourceProfile, LogicalEventBinding?>> BuildAccessors()
    {
        Dictionary<string, Func<DemoSourceProfile, LogicalEventBinding?>> map = new(
            StringComparer.OrdinalIgnoreCase);

        foreach (PropertyInfo prop in typeof(DemoSourceProfile)
                     .GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType != typeof(LogicalEventBinding))
            {
                continue;
            }

            string snake = ToSnakeCase(prop.Name);
            map[snake] = profile => (LogicalEventBinding?)prop.GetValue(profile);
        }

        return map;
    }

    /// <summary>
    ///     <c>RoundOfficiallyEnded</c> → <c>round_officially_ended</c>,
    ///     <c>HeGrenadeDetonate</c> → <c>he_grenade_detonate</c>,
    ///     <c>HltvChase</c> → <c>hltv_chase</c>.
    /// </summary>
    /// <remarks>
    ///     Insert <c>_</c> before every uppercase letter that is preceded by
    ///     a lowercase letter, then lowercase. Acronym runs collapse: e.g.
    ///     <c>Hltv</c> emits <c>hltv</c> (no inner underscores) because only
    ///     <c>H</c> is uppercase.
    /// </remarks>
    private static string ToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
        {
            return pascalCase;
        }

        StringBuilder sb = new(pascalCase.Length + 4);
        for (int i = 0; i < pascalCase.Length; i++)
        {
            char c = pascalCase[i];
            if (i > 0 && char.IsUpper(c) && char.IsLower(pascalCase[i - 1]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }
}
