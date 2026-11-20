using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>maxItems</c> and <c>minItems</c> (2020-12 Validation §6.4.1-2).
/// </summary>
internal sealed class ItemCountKeyword : SchemaKeyword
{
    public ItemCountKeyword(SchemaNode owner, string name, JsonElement value, long limit, bool upper)
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
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        int count = instance.GetArrayLength();
        if (Upper ? count <= Limit : count >= Limit)
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Upper ? Resources.MaxItemsExceeded : Resources.MinItemsNotMet, Limit));
        }

        return false;
    }
}
