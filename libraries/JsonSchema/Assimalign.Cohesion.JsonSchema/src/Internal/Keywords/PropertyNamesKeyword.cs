using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>propertyNames</c> (2020-12 Core §10.3.2.4): every member name, as a string instance, must be
/// valid against the subschema. Errors are reported at the member's location.
/// </summary>
internal sealed class PropertyNamesKeyword : SchemaKeyword
{
    public PropertyNamesKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!context.RecordsOutput && Subschema.BooleanValue is bool value)
        {
            return value || instance.GetPropertyCount() == 0;
        }

        bool valid = true;
        foreach (JsonProperty member in instance.EnumerateObject())
        {
            if (!context.EvaluateDerived(Subschema, JsonValues.CreateString(member.Name), member))
            {
                if (context.ShortCircuit)
                {
                    return false;
                }

                valid = false;
            }
        }

        if (!valid)
        {
            context.Fail(Resources.PropertyNamesFailed);
        }

        return valid;
    }
}
