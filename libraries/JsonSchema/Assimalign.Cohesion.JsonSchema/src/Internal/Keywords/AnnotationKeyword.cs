using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// A keyword whose only effect is its value as an annotation: the meta-data vocabulary
/// (<c>title</c>, <c>description</c>, <c>default</c>, <c>examples</c>, <c>deprecated</c>,
/// <c>readOnly</c>, <c>writeOnly</c>) and, from 2019-09, unknown keywords (2020-12 Core §7.7.1.1).
/// </summary>
internal sealed class AnnotationKeyword : SchemaKeyword
{
    public AnnotationKeyword(SchemaNode owner, string name, JsonElement value)
        : base(owner, name, value)
    {
    }

    public override int Priority => KeywordPriority.Annotation;

    public override bool IsAnnotationOnly => true;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        context.Annotate(Value);
        return true;
    }
}
