using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>const</c> (2020-12 Validation §6.1.3): the instance must equal the value, by JSON Schema equality.
/// </summary>
internal sealed class ConstKeyword : SchemaKeyword
{
    public ConstKeyword(SchemaNode owner, string name, JsonElement value)
        : base(owner, name, value)
    {
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (JsonEquality.AreEqual(Value, instance))
        {
            return true;
        }

        context.Fail(Resources.ConstMismatch);
        return false;
    }
}
