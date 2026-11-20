using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>not</c>: the instance must be invalid against the subschema (2020-12 Core §10.2.1.4). The
/// subschema's annotations never count, since they would come from a schema that must fail.
/// </summary>
internal sealed class NotKeyword : SchemaKeyword
{
    public NotKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (!context.EvaluateInPlace(Subschema, instance, keepAnnotations: false))
        {
            return true;
        }

        context.Fail(Resources.NotFailed);
        return false;
    }
}
