using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// The two ways the array applicators apply subschemas: position by position (<c>prefixItems</c>,
/// array-form <c>items</c>) and one subschema for every item from an index on (<c>items</c>,
/// <c>additionalItems</c>).
/// </summary>
internal static class ItemSequence
{
    /// <summary>
    /// Applies <paramref name="subschemas"/> to the leading items. The annotation is the largest index
    /// applied, or <see langword="true"/> when every item was covered.
    /// </summary>
    public static bool EvaluateTuple(EvaluationContext context, JsonElement instance, SchemaNode[] subschemas, string error)
    {
        bool valid = true;
        int index = 0;
        foreach (JsonElement item in instance.EnumerateArray())
        {
            if (index >= subschemas.Length)
            {
                break;
            }

            if (!context.EvaluateItem(subschemas[index], item, index))
            {
                if (context.ShortCircuit)
                {
                    return false;
                }

                valid = false;
            }

            index++;
        }

        if (!valid)
        {
            context.Fail(error);
            return false;
        }

        context.MarkItems(0, index);
        if (context.RecordsOutput && index > 0)
        {
            context.Annotate(index == instance.GetArrayLength() ? JsonValues.True : JsonValues.CreateNumber(index - 1));
        }

        return true;
    }

    /// <summary>
    /// Applies <paramref name="subschema"/> to every item from <paramref name="start"/> on. The
    /// annotation is <see langword="true"/> when it applied to any item.
    /// </summary>
    public static bool EvaluateRest(EvaluationContext context, JsonElement instance, SchemaNode subschema, int start, string error, string forbidden)
    {
        int length = instance.GetArrayLength();
        if (start >= length)
        {
            return true;
        }

        if (context.ShortCircuit && subschema.BooleanValue is bool value)
        {
            if (value)
            {
                context.MarkItems(start, length);
            }

            return value;
        }

        bool valid = true;
        int index = 0;
        foreach (JsonElement item in instance.EnumerateArray())
        {
            if (index >= start && !context.EvaluateItem(subschema, item, index))
            {
                if (context.ShortCircuit)
                {
                    return false;
                }

                valid = false;
            }

            index++;
        }

        if (!valid)
        {
            context.Fail(subschema.BooleanValue == false ? forbidden : error);
            return false;
        }

        context.MarkItems(start, length);
        context.Annotate(JsonValues.True);
        return true;
    }
}
