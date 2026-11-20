using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>dependentSchemas</c> (2020-12 Core §10.2.2.4): for each member the keyword names that the
/// instance has, the whole instance must be valid against that member's subschema.
/// </summary>
internal sealed class DependentSchemasKeyword : SchemaKeyword
{
    public DependentSchemasKeyword(SchemaNode owner, string name, JsonElement value, Dictionary<string, SchemaNode> subschemas)
        : base(owner, name, value)
    {
        Subschemas = subschemas;
    }

    public Dictionary<string, SchemaNode> Subschemas { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        bool valid = true;
        foreach (KeyValuePair<string, SchemaNode> dependency in Subschemas)
        {
            if (!instance.TryGetProperty(dependency.Key, out _))
            {
                continue;
            }

            if (!context.EvaluateInPlace(dependency.Value, instance))
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
            context.Fail(Resources.DependentSchemasFailed);
        }

        return valid;
    }
}
