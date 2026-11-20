using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>if</c> (2020-12 Core §10.2.2.1): never fails; its outcome selects whether <c>then</c> or
/// <c>else</c> applies, and its annotations count when the instance is valid against it.
/// </summary>
internal sealed class IfKeyword : SchemaKeyword
{
    public IfKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema)
        : base(owner, name, value)
    {
        Subschema = subschema;
    }

    public SchemaNode Subschema { get; }

    /// <summary>Gets a value indicating whether the schema has a <c>then</c> or an <c>else</c>.</summary>
    public bool HasBranch { get; private set; }

    public override int Priority => KeywordPriority.Applicator;

    public override void Link(List<SchemaKeyword> siblings)
    {
        foreach (SchemaKeyword sibling in siblings)
        {
            HasBranch |= sibling is ConditionalBranchKeyword;
        }
    }

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        if (!HasBranch && !context.CollectsAnnotations)
        {
            return true;
        }

        context.Frame.Conditional = context.EvaluateInPlace(Subschema, instance) ? Frame.Condition.Passed : Frame.Condition.Failed;
        return true;
    }
}
