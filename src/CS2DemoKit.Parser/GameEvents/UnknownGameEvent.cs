#region

using System.Globalization;

#endregion

namespace CS2DemoKit.Parser.GameEvents;

/// <summary>
///     Fallback for events that have no typed subtype.
///     <see cref="Fields" /> is keyed by the schema field name.
/// </summary>
public sealed record UnknownGameEvent(
    string Name,
    int EventId,
    int FrameNumber,
    int ServerTick,
    int GameTick,
    IReadOnlyDictionary<string, object> Fields) : GameEvent(Name, EventId, FrameNumber, ServerTick, GameTick)
{
    private IReadOnlyList<(string, string, string)>? _decodedFields;

    /// <inheritdoc />
    public override IReadOnlyList<(string, string, string)> GetDecodedFields() =>
        _decodedFields ??= BuildDecoded();

    private List<(string, string, string)> BuildDecoded()
    {
        List<(string, string, string)> list = new(Fields.Count);
        foreach ((string key, object val) in Fields)
        {
            (string fmtVal, string wireType) = val switch
            {
                bool b => (b ? "True" : "False", "bool"),
                float f => (f.ToString("G", CultureInfo.InvariantCulture), "float"),
                int i => (i.ToString(CultureInfo.InvariantCulture), "int"),
                long l => (l.ToString(CultureInfo.InvariantCulture), "int"),
                short s => (s.ToString(CultureInfo.InvariantCulture), "int"),
                ulong u => (u.ToString(CultureInfo.InvariantCulture), "uint64"),
                string s => ($"\"{s}\"", "string"),
                _ => (val?.ToString() ?? "", "")
            };
            list.Add((key, fmtVal, wireType));
        }

        return list;
    }
}
