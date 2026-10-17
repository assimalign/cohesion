using System;
using System.Collections.Generic;
using System.Linq;
using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal static class GraphValueExpressions
{
    internal static IEnumerable<GqlExpression> Children(GqlExpression expression)
    {
        switch (expression)
        {
            case GqlCaseExpression conditional:
                if (conditional.Alternatives is null || conditional.Alternatives.Count == 0 || conditional.Alternatives.Any(branch => branch is null))
                { throw new GraphPlanningException("COHDBG001", ErrorMessages.CaseInvalid, expression.Location); }
                if (conditional.Operand is not null) { yield return conditional.Operand; }
                foreach (var branch in conditional.Alternatives) { yield return branch.When; yield return branch.Then; }
                if (conditional.Otherwise is not null) { yield return conditional.Otherwise; }
                break;
            case GqlCoalesceExpression coalesce:
                if (coalesce.Operands is null || coalesce.Operands.Count < 2)
                { throw new GraphPlanningException("COHDBG001", ErrorMessages.CaseInvalid, expression.Location); }
                foreach (var operand in coalesce.Operands) { yield return operand; }
                break;
            case GqlNullIfExpression nullIf: yield return nullIf.Left; yield return nullIf.Right; break;
            case GqlListExpression list:
                if (list.Items is null) { throw new GraphPlanningException("COHDBG001", ErrorMessages.MembershipListRequired, expression.Location); }
                foreach (var item in list.Items) { yield return item; }
                break;
            case GqlMapExpression map:
                if (map.Entries is null || map.Entries.Any(entry => entry.Key is null) ||
                    map.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() != map.Entries.Count)
                { throw new GraphPlanningException("COHDBG001", ErrorMessages.CompositeRequired, expression.Location); }
                foreach (var entry in map.Entries) { yield return entry.Value; }
                break;
            case GqlSubscriptExpression subscript: yield return subscript.Operand; yield return subscript.Index; break;
            case GqlMemberExpression member:
                if (member.Member is null) { throw new GraphPlanningException("COHDBG001", ErrorMessages.CompositeRequired, expression.Location); }
                yield return member.Operand; break;
        }
    }

    // Only value-producing branches propagate entities: comparisons and tests produce scalars.
    internal static IEnumerable<GqlExpression> ResultChildren(GqlExpression expression)
    {
        if (expression is GqlCaseExpression conditional)
        {
            foreach (var branch in conditional.Alternatives) { yield return branch.Then; }
            if (conditional.Otherwise is not null) { yield return conditional.Otherwise; }
        }
        else if (expression is GqlNullIfExpression nullIf) { yield return nullIf.Left; }
        else if (expression is GqlListExpression or GqlMapExpression or GqlCoalesceExpression or GqlSubscriptExpression or GqlMemberExpression)
        { foreach (var child in Children(expression)) { yield return child; } }
    }
}
