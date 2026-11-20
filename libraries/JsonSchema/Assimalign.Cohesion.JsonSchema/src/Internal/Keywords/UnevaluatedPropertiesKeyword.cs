using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>unevaluatedProperties</c> (2020-12 Core §11.3): members that no adjacent keyword, and no valid
/// in-place subschema, evaluated must be valid against the subschema. It runs after every other
/// keyword of its schema. The annotation is the names it applied to.
/// </summary>
internal sealed class UnevaluatedPropertiesKeyword : SchemaKeyword
{
    public UnevaluatedPropertiesKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public override int Priority => KeywordPriority.Unevaluated;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        EvaluatedSet evaluated = context.Frame.Properties;
        List<string>? applied = null;
        bool valid = true;
        int position = 0;
        foreach (JsonProperty member in instance.EnumerateObject())
        {
            if (!evaluated.Contains(position))
            {
                context.MarkProperty(position);
                if (context.RecordsOutput)
                {
                    (applied ??= new()).Add(member.Name);
                }

                if (!context.EvaluateProperty(Subschema, member))
                {
                    if (context.ShortCircuit)
                    {
                        return false;
                    }

                    valid = false;
                }
            }

            position++;
        }

        if (!valid)
        {
            context.Fail(Subschema.BooleanValue == false ? Resources.UnevaluatedPropertiesNotAllowed : Resources.UnevaluatedPropertiesFailed);
            return false;
        }

        if (applied is not null)
        {
            context.Annotate(JsonValues.CreateStringArray(applied));
        }

        return true;
    }
}
