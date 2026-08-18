#region

using System.Linq.Expressions;
using System.Reflection;

using CS2DemoKit.Parser.GameEvents;

#endregion

namespace CS2DemoKit.Analysis.Registry;

/// <summary>
///     Cached accessor for one property on a game-event record: the field name, its declared type,
///     and a compiled boxed getter. Built once via <see cref="EventRegistry.Build" /> and reused.
/// </summary>
public sealed class EventFieldAccessor
{
    private EventFieldAccessor(string fieldName, Type fieldType, Func<object, object?> getValue)
    {
        FieldName = fieldName;
        FieldType = fieldType;
        GetValue = getValue;
    }

    /// <summary>The property name as declared on the game-event record.</summary>
    public string FieldName { get; }

    /// <summary>The property's CLR type.</summary>
    public Type FieldType { get; }

    /// <summary>Compiled delegate that reads the property off a boxed event instance and returns the boxed value.</summary>
    public Func<object, object?> GetValue { get; }

    internal static EventFieldAccessor FromProperty(PropertyInfo prop)
    {
        ParameterExpression param = Expression.Parameter(typeof(object), "obj");

        // Callers hand this the FIRE (a GameEvent), but a wire event's fields live on the SDK
        // payload record hanging off it. Unwrap before the cast, decided once at build time rather
        // than per read.
        //
        // Synthesized events are the exception and must NOT be unwrapped: the analysis layer
        // derives them (molotov_thrown from a projectile's thrower handle) as GameEvent subclasses
        // that declare their fields directly and carry no payload.
        bool declaredOnTheFire = typeof(GameEvent).IsAssignableFrom(prop.DeclaringType);

        Expression source = declaredOnTheFire
            ? param
            : Expression.Condition(
                Expression.TypeIs(param, typeof(GameEvent)),
                Expression.Property(
                    Expression.Convert(param, typeof(GameEvent)), nameof(GameEvent.Payload)),
                param);

        UnaryExpression cast = Expression.Convert(source, prop.DeclaringType!);
        MemberExpression access = Expression.Property(cast, prop);

        // Widen the narrow integral widths to int before boxing. The SDK types each field to its
        // KV1 tag, so byte and short reach here where the retired generator emitted int
        // everywhere. Expression trees do not apply C#'s numeric promotions, so leaving them
        // narrow makes `event.DmgHealth + event.DmgArmor` fail to compile at rule-build time with
        // "the binary operator Add is not defined for System.Int16".
        Type widened = prop.PropertyType;
        Expression value = access;
        if (widened == typeof(byte) || widened == typeof(sbyte)
            || widened == typeof(short) || widened == typeof(ushort))
        {
            value = Expression.Convert(access, typeof(int));
            widened = typeof(int);
        }

        UnaryExpression boxed = Expression.Convert(value, typeof(object));
        Func<object, object?> lambda = Expression.Lambda<Func<object, object?>>(boxed, param).Compile();

        return new EventFieldAccessor(prop.Name, widened, lambda);
    }
}
