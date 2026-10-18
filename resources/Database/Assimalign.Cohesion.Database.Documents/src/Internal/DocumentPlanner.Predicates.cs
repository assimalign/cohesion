using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    internal static IEnumerable<OqlExpression> PredicateChildren(OqlExpression expression)
    {
        switch (expression)
        {
            case OqlQuantifiedExpression quantified:
                yield return quantified.Collection;
                yield return quantified.Predicate;
                break;
            case OqlInExpression membership:
                yield return membership.Operand;
                if (membership.Array is { } array) { yield return array; }
                foreach (var value in membership.Values) { yield return value; }
                break;
            case OqlBetweenExpression between:
                yield return between.Operand;
                yield return between.Lower;
                yield return between.Upper;
                break;
            case OqlLikeExpression like:
                yield return like.Operand;
                yield return like.Pattern;
                if (like.Escape is not null) { yield return like.Escape; }
                break;
            case OqlCaseExpression conditional:
                if (conditional.Operand is not null) { yield return conditional.Operand; }
                foreach (var branch in conditional.Branches) { yield return branch.Key; yield return branch.Value; }
                if (conditional.Otherwise is not null) { yield return conditional.Otherwise; }
                break;
        }
    }
}
