using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>maxLength</c> and <c>minLength</c> (2020-12 Validation §6.3.1-2): a string's length in Unicode
/// code points, so an astral character counts once.
/// </summary>
internal sealed class LengthKeyword : SchemaKeyword
{
    public LengthKeyword(SchemaNode owner, string name, JsonElement value, long limit, bool upper)
        : base(owner, name, value)
    {
        Limit = limit;
        Upper = upper;
    }

    public long Limit { get; }

    /// <summary>Gets a value indicating whether the limit is a maximum.</summary>
    public bool Upper { get; }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.String)
        {
            return true;
        }

        // The raw text, less its quotes, is never shorter than the code points it encodes.
        if (Upper && JsonMarshal.GetRawUtf8Value(instance).Length - 2 <= Limit)
        {
            return true;
        }

        long length = JsonValues.CountCodePoints(instance);
        if (Upper ? length <= Limit : length >= Limit)
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Upper ? Resources.MaxLengthExceeded : Resources.MinLengthNotMet, Limit));
        }

        return false;
    }
}
