#region

using System.Diagnostics.CodeAnalysis;
using CS2DemoKit.Parser;
using Google.Protobuf.WellKnownTypes;

#endregion

namespace CS2DemoKit.Analysis.Abstractions;

/// <summary>
///     Carrier for a synthesized <see cref="EntityValueChangedEvent" />. Subclasses
///     <see cref="NetMessage" /> so the evaluator's existing message-dispatch loop processes
///     these uniformly with real net messages — no special-casing in the hot path beyond a
///     single <c>GetDispatchKey</c> branch that returns <c>ChangeEvent.GetType()</c> in place
///     of the placeholder <c>Payload</c>.
///     <para>
///         The base class requires a non-null <c>IMessage Payload</c>. We use <see cref="Empty" />
///         as a placeholder; rule-side code reads <see cref="ChangeEvent" /> instead.
///     </para>
/// </summary>
public sealed class EntityChangeMessage : NetMessage
{
    /// <summary>Shared placeholder so all synthesized messages share one allocation.</summary>
    public static readonly Empty PayloadPlaceholder = new();

    /// <summary>Initializes a new <see cref="EntityChangeMessage" /> instance.</summary>
    [SetsRequiredMembers]
    public EntityChangeMessage(EntityValueChangedEvent changeEvent)
    {
        MessageTypeName = "EntityChange";
        Payload = PayloadPlaceholder;
        ChangeEvent = changeEvent;
    }

    /// <summary>The synthesized change event. Concrete type is <c>EntityValueChangedEvent&lt;TMarker&gt;</c>.</summary>
    public EntityValueChangedEvent ChangeEvent { get; }
}
