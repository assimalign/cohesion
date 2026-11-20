using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>multipleOf</c> (2020-12 Validation §6.2.1): a number must be an integer multiple of the divisor,
/// computed exactly in decimal so <c>0.0075</c> is a multiple of <c>0.0001</c>.
/// </summary>
internal sealed class MultipleOfKeyword : SchemaKeyword
{
    public MultipleOfKeyword(SchemaNode owner, string name, JsonElement value, JsonNumber divisor)
        : base(owner, name, value)
    {
        Divisor = divisor;
        if (value.TryGetInt64(out long integer))
        {
            DivisorInt64 = integer;
        }
    }

    public JsonNumber Divisor { get; }

    /// <summary>Gets the divisor when it is written as an integer that fits in 64 bits.</summary>
    public long? DivisorInt64 { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Number)
        {
            return true;
        }

        bool valid = DivisorInt64 is long divisor && instance.TryGetInt64(out long value)
            ? value % divisor == 0
            : JsonNumber.FromElement(instance).IsMultipleOf(Divisor);
        if (valid)
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.MultipleOfFailed, Value.GetRawText()));
        }

        return false;
    }
}
