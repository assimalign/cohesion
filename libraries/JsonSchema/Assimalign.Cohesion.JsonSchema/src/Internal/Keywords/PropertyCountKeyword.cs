using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>maxProperties</c> and <c>minProperties</c> (2020-12 Validation §6.5.1-2).
/// </summary>
internal sealed class PropertyCountKeyword : SchemaKeyword
{
    public PropertyCountKeyword(SchemaNode owner, string name, JsonElement value, long limit, bool upper)
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
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        int count = instance.GetPropertyCount();
        if (Upper ? count <= Limit : count >= Limit)
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Upper ? Resources.MaxPropertiesExceeded : Resources.MinPropertiesNotMet, Limit));
        }

        return false;
    }
}
