using System.Globalization;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// <c>oneOf</c>: the instance must be valid against exactly one subschema (2020-12 Core §10.2.1.3).
/// </summary>
internal sealed class OneOfKeyword : SchemaKeyword
{
    public OneOfKeyword(SchemaNode owner, string name, JsonElement value, SchemaNode[] subschemas)
        : base(owner, name, value)
    {
        Subschemas = subschemas;
    }

    public SchemaNode[] Subschemas { get; }

    public override int Priority => KeywordPriority.Applicator;

    public override bool Evaluate(EvaluationContext context, JsonElement instance)
    {
        int first = -1;
        int second = -1;
        for (int i = 0; i < Subschemas.Length; i++)
        {
            if (!context.EvaluateInPlace(Subschemas[i], instance))
            {
                continue;
            }

            if (first < 0)
            {
                first = i;
            }
            else
            {
                if (second < 0)
                {
                    second = i;
                }

                if (context.ShortCircuit)
                {
                    return false;
                }
            }
        }

        if (first < 0)
        {
            context.Fail(Resources.OneOfNoneMatched);
            return false;
        }

        if (second >= 0)
        {
            if (context.RecordsOutput)
            {
                context.Fail(string.Format(CultureInfo.CurrentCulture, Resources.OneOfManyMatched, first, second));
            }

            return false;
        }

        return true;
    }
}
