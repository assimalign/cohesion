using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>contains</c> with its <c>minContains</c> and <c>maxContains</c> siblings (2020-12 Core
/// §10.3.1.3, Validation §6.4.4-5): the number of items valid against the subschema must fall within
/// the bounds, by default at least one.
/// </summary>
/// <remarks>
/// From 2020-12 the annotation is the indexes of the matching items, and <c>unevaluatedItems</c>
/// treats them as evaluated. Earlier dialects neither annotate nor mark.
/// </remarks>
internal sealed class ContainsKeyword : SchemaKeyword
{
    public ContainsKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema, long minimum, long? maximum, bool annotates)
        : base(owner, name, value)
    {
        Subschema = subschema;
        Minimum = minimum;
        Maximum = maximum;
        Annotates = annotates;
    }

    public SchemaNode Subschema { get; }

    public long Minimum { get; }

    public long? Maximum { get; }

    public bool Annotates { get; }

    public override int Priority => KeywordPriority.Items;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        bool collect = context.RecordsOutput || (Annotates && context.Frame.Tracks);
        if (!collect && Minimum == 0 && Maximum is null)
        {
            return true;
        }

        long matches = 0;
        List<int>? indexes = null;
        int index = 0;
        foreach (JsonElement item in instance.EnumerateArray())
        {
            if (context.EvaluateItem(Subschema, item, index))
            {
                matches++;
                if (Annotates)
                {
                    context.MarkItem(index);
                    if (context.RecordsOutput)
                    {
                        (indexes ??= new()).Add(index);
                    }
                }

                if (!collect && Maximum is null && matches >= Minimum)
                {
                    return true;
                }

                if (context.ShortCircuit && matches > Maximum)
                {
                    return false;
                }
            }

            index++;
        }

        if (matches < Minimum)
        {
            if (context.RecordsOutput)
            {
                context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.ContainsTooFew, Minimum));
            }

            return false;
        }

        if (matches > Maximum)
        {
            if (context.RecordsOutput)
            {
                context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.ContainsTooMany, Maximum));
            }

            return false;
        }

        if (indexes is not null)
        {
            context.Annotate(JsonValues.CreateNumberArray(indexes));
        }

        return true;
    }
}
