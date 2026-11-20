using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>items</c> as drafts 4 to 2019-09 define it: a schema every item must satisfy, or an array of
/// schemas the leading items must satisfy position by position.
/// </summary>
internal sealed class LegacyItemsKeyword : SchemaKeyword
{
    public LegacyItemsKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode? subschema, SchemaNode[]? subschemas)
        : base(owner, name, value)
    {
        Subschema = subschema;
        Subschemas = subschemas;
    }

    /// <summary>Gets the schema every item must satisfy, or <see langword="null"/> for the array form.</summary>
    public SchemaNode? Subschema { get; }

    /// <summary>Gets the positional schemas of the array form, or <see langword="null"/>.</summary>
    public SchemaNode[]? Subschemas { get; }

    public override int Priority => KeywordPriority.Items;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (instance.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        return Subschema is not null
            ? ItemSequence.EvaluateRest(context, instance, Subschema, 0, Resources.ItemsFailed, Resources.ItemsNotAllowed)
            : ItemSequence.EvaluateTuple(context, instance, Subschemas!, Resources.ItemsFailed);
    }
}
