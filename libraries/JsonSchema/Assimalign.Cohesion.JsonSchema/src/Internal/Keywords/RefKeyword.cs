using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>$ref</c>: applies the referenced schema to the instance (2020-12 Core §8.2.3.1). Before
/// 2019-09 the compiler drops its siblings, so it replaces its schema object.
/// </summary>
internal sealed class RefKeyword : ReferenceKeyword
{
    public RefKeyword(SchemaNode owner, string name, JsonElement value, UriReference reference)
        : base(owner, name, value, reference)
    {
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance) =>
        Apply(context, GetTarget(), instance);
}
