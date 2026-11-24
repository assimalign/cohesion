using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>unevaluatedItems</c> (2020-12 Core §11.2): items that no adjacent keyword, and no valid in-place
/// subschema, evaluated must be valid against the subschema. The annotation is
/// <see langword="true"/> when it applied to any item.
/// </summary>
internal sealed class UnevaluatedItemsKeyword : SchemaKeyword
{
    public UnevaluatedItemsKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public override int Priority => KeywordPriority.Unevaluated;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        EvaluatedSet evaluated = context.Frame.Items;
        bool applied = false;
        bool valid = true;
        int index = 0;
        foreach (JsonElement item in instance.EnumerateArray())
        {
            if (!evaluated.Contains(index))
            {
                applied = true;
                context.MarkItem(index);
                if (!context.EvaluateItem(Subschema, item, index))
                {
                    if (context.ShortCircuit)
                    {
                        return false;
                    }

                    valid = false;
                }
            }

            index++;
        }

        if (!valid)
        {
            context.Fail(Subschema.BooleanValue == false ? Resources.UnevaluatedItemsNotAllowed : Resources.UnevaluatedItemsFailed);
            return false;
        }

        if (applied)
        {
            context.Annotate(JsonValues.True);
        }

        return true;
    }
}
