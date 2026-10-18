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
                if (conditional.Alternatives is null || conditional.Alternatives.Count == 0 || conditional.Alternatives.Any(branch => branch is null || branch.Comparison is not ("=" or "<>" or "!=" or "<" or "<=" or ">" or ">=" or "IS NULL" or "IS NOT NULL")))
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

    // Access consumes a container level; accessing an entity produces a scalar property.
    internal static bool MayReturn(GqlExpression expression, IReadOnlyDictionary<string, object?>? parameters, bool entity)
    {
        var pending = new Stack<(GqlExpression Expression, int Accesses)>();
        pending.Push((expression, 0));
        while (pending.TryPop(out var item))
        {
            var (value, accesses) = item;
            switch (value)
            {
                case GqlVariableExpression:
                    if (entity && accesses == 0) { return true; }
                    break;
                case GqlMemberExpression member: pending.Push((member.Operand, accesses + 1)); break;
                case GqlSubscriptExpression subscript: pending.Push((subscript.Operand, accesses + 1)); break;
                case GqlListExpression or GqlMapExpression:
                    if (!entity && accesses == 0) { return true; }
                    foreach (var child in Children(value)) { pending.Push((child, Math.Max(0, accesses - 1))); }
                    break;
                case GqlParameterExpression parameter when !entity:
                    if (parameters is not null && parameters.TryGetValue(parameter.Name, out var binding) && MayContain(binding, accesses)) { return true; }
                    break;
                case GqlLiteralExpression literal when !entity:
                    if (MayContain(literal.Value, accesses)) { return true; }
                    break;
                case GqlCaseExpression or GqlCoalesceExpression or GqlNullIfExpression:
                    foreach (var child in ResultChildren(value)) { pending.Push((child, accesses)); }
                    break;
            }
        }
        return false;
    }

    private static bool MayContain(object? value, int accesses)
    {
        var pending = new Stack<(object? Value, int Accesses)>(); pending.Push((value, accesses));
        while (pending.TryPop(out var item))
        {
            if (item.Accesses == 0)
            {
                if (GraphCompositeValues.IsComposite(item.Value) || item.Value is byte or ushort or uint or ulong) { return true; }
            }
            else if (item.Value is object?[] list)
            { foreach (var child in list) { pending.Push((child, item.Accesses - 1)); } }
            else if (item.Value is IReadOnlyDictionary<string, object?> map)
            { foreach (var child in map.Values) { pending.Push((child, item.Accesses - 1)); } }
        }
        return false;
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
