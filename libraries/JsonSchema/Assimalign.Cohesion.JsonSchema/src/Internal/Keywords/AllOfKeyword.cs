using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>allOf</c>: the instance must be valid against every subschema (2020-12 Core §10.2.1.1).
/// </summary>
internal sealed class AllOfKeyword : SchemaKeyword
{
    public AllOfKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode[] subschemas)
        : base(owner, name, value)
    {
        Subschemas = subschemas;
    }

    public SchemaNode[] Subschemas { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        bool valid = true;
        foreach (SchemaNode subschema in Subschemas)
        {
            if (!context.EvaluateInPlace(subschema, instance))
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
            context.Fail(Resources.AllOfFailed);
        }

        return valid;
    }
}
