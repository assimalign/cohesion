using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>anyOf</c>: the instance must be valid against at least one subschema (2020-12 Core §10.2.1.2).
/// Every subschema is applied when annotations are collected, because each valid one contributes.
/// </summary>
internal sealed class AnyOfKeyword : SchemaKeyword
{
    public AnyOfKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode[] subschemas)
        : base(owner, name, value)
    {
        Subschemas = subschemas;
    }

    public SchemaNode[] Subschemas { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        bool collect = context.CollectsAnnotations;
        bool valid = false;
        foreach (SchemaNode subschema in Subschemas)
        {
            if (context.EvaluateInPlace(subschema, instance))
            {
                valid = true;
                if (!collect)
                {
                    return true;
                }
            }
        }

        if (!valid)
        {
            context.Fail(Resources.AnyOfFailed);
        }

        return valid;
    }
}
