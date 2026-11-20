using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>$recursiveRef</c> (2019-09 Core §8.2.4.2): when its target is a resource root declaring
/// <c>"$recursiveAnchor": true</c>, the target moves to the outermost resource in the dynamic scope
/// that declares the same; otherwise it behaves like <c>$ref</c>.
/// </summary>
internal sealed class RecursiveRefKeyword : ReferenceKeyword
{
    public RecursiveRefKeyword(SchemaNode owner, string name, JsonElement value, UriReference reference)
        : base(owner, name, value, reference)
    {
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        SchemaNode target = GetTarget();
        if (target.IsResourceRoot && target.Resource.RecursiveAnchor)
        {
            target = context.FindRecursiveAnchor() ?? target;
        }

        return Apply(context, target, instance);
    }
}
