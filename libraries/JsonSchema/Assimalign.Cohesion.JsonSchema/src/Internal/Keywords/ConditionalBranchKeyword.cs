using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>then</c> and <c>else</c> (2020-12 Core §10.2.2.2-3): applied when the sibling <c>if</c> passed
/// or failed respectively, and ignored without an <c>if</c>.
/// </summary>
internal sealed class ConditionalBranchKeyword : SchemaKeyword
{
    public ConditionalBranchKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode subschema, bool isThen)
        : base(owner, name, value)
    {
        Subschema = subschema;
        IsThen = isThen;
    }

    public SchemaNode Subschema { get; }

    public bool IsThen { get; }

    public override int Priority => KeywordPriority.ConditionalBranch;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        Frame.Condition condition = context.Frame.Conditional;
        if (condition == Frame.Condition.None || (condition == Frame.Condition.Passed) != IsThen)
        {
            return true;
        }

        if (context.EvaluateInPlace(Subschema, instance))
        {
            return true;
        }

        if (context.RecordsOutput)
        {
            context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.ConditionalBranchFailed, Name));
        }

        return false;
    }
}
