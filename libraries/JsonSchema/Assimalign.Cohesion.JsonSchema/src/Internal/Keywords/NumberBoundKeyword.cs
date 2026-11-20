using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>maximum</c>, <c>exclusiveMaximum</c>, <c>minimum</c> and <c>exclusiveMinimum</c> (2020-12
/// Validation §6.2.2-5), and the draft 4 form where a boolean sibling makes the bound exclusive.
/// Numbers compare by exact value.
/// </summary>
internal sealed class NumberBoundKeyword : SchemaKeyword
{
    public NumberBoundKeyword(SchemaNode owner, string name, JsonElement value, JsonNumber bound, bool upper, bool exclusive)
        : base(owner, name, value)
    {
        Bound = bound;
        Upper = upper;
        Exclusive = exclusive;
        if (value.TryGetInt64(out long integer))
        {
            BoundInt64 = integer;
        }
    }

    public JsonNumber Bound { get; }

    /// <summary>Gets a value indicating whether the bound is a maximum.</summary>
    public bool Upper { get; }

    public bool Exclusive { get; }

    /// <summary>Gets the bound when it is written as an integer that fits in 64 bits.</summary>
    public long? BoundInt64 { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Number)
        {
            return true;
        }

        int comparison = BoundInt64 is long bound && instance.TryGetInt64(out long value)
            ? value.CompareTo(bound)
            : JsonNumber.FromElement(instance).CompareTo(Bound);
        bool valid = Upper
            ? (Exclusive ? comparison < 0 : comparison <= 0)
            : (Exclusive ? comparison > 0 : comparison >= 0);
        if (valid)
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            string format = Upper
                ? (Exclusive ? Resources.ExclusiveMaximumExceeded : Resources.MaximumExceeded)
                : (Exclusive ? Resources.ExclusiveMinimumNotMet : Resources.MinimumNotMet);
            context.Fail(string.Format(CultureInfo.CurrentCulture, format, Value.GetRawText()));
        }

        return false;
    }
}
