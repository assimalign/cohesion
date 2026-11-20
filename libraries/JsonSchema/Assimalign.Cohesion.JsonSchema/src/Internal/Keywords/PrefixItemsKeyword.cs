using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>prefixItems</c> (2020-12 Core §10.3.1.1): each leading item must be valid against the subschema
/// at its index. The annotation is the largest index applied, or <see langword="true"/> when it
/// applied to every item.
/// </summary>
internal sealed class PrefixItemsKeyword : SchemaKeyword
{
    public PrefixItemsKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode[] subschemas)
        : base(owner, name, value)
    {
        Subschemas = subschemas;
    }

    public SchemaNode[] Subschemas { get; }

    public override int Priority => KeywordPriority.Items;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return ItemSequence.EvaluateTuple(context, instance, Subschemas, Resources.PrefixItemsFailed);
    }
}
