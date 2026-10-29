using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    private static DatabaseType GroupProjectionType(OqlExpression expression, OqlSelectExpression query, DocumentFunctionCatalog functions)
    {
        if (expression is OqlPathExpression { Segments.Count: 1 } path)
        {
            string? name = path.Segments[0].Name;
            if (query.GroupAs is not null && name == query.GroupAs) { return DatabaseType.Json; }
            for (int index = 0; index < query.GroupAliases.Count; index++)
            { if (query.GroupAliases[index] == name) { return StaticType(query.GroupBy[index], functions); } }
        }
        return StaticType(expression, functions);
    }

    private static bool EquivalentGroupedExpression(OqlExpression left, OqlExpression right, OqlSelectExpression query,
        IReadOnlySet<string>? variables, DocumentBindingScopes scopes)
    {
        // Do not capture a new group-frame local in ordinary planning's existing lambdas:
        // even one additional closure slot would change its pinned allocation measurements.
        var bound = scopes.FindGroup(query);
        return EquivalentExpression(left, right, query.Alias, variables, scopes, bound, bound);
    }

    private static DocumentBindingScope? GroupScope(OqlSelectExpression query, IReadOnlySet<string>? unnestVariables)
    {
        if (query.GroupBy.Count == 0 || query.GroupAliases.Count != 0 && query.GroupAliases.Count != query.GroupBy.Count)
        { throw new DocumentPlanningException("CD01030", ErrorMessages.GroupBindingInvalid, query.Location); }
        var names = new HashSet<string>(StringComparer.Ordinal) { query.Alias ?? query.Collection };
        if (unnestVariables is not null) { names.UnionWith(unnestVariables); }
        DocumentBindingScope? scope = null;
        foreach (var name in query.GroupAliases) { if (name is not null) { Add(name); } }
        if (query.GroupAs is not null) { Add(query.GroupAs); }
        return scope;

        void Add(string name)
        {
            if (string.IsNullOrEmpty(name) || !names.Add(name))
            { throw new DocumentPlanningException("CD01030", ErrorMessages.GroupBindingInvalid, query.Location); }
            scope = new DocumentBindingScope(name, scope);
        }
    }

    // GROUP AS exists only after aggregation. An aggregate's row input cannot read it;
    // a comprehension/quantifier introducing the same name still owns its local paths.
    private static void ValidateGroupArrayInput(OqlExpression expression, OqlSelectExpression query, DocumentBindingScopes scopes, DocumentFunctionCatalog functions,
        bool aggregateInput = false, DocumentBindingScope? local = null)
    {
        if (expression is OqlSubqueryExpression subquery && aggregateInput)
        {
            ValidateGroupedCaptures(subquery.Query, query, null, scopes, functions, local, null, groupArrayInput: true);
            return;
        }
        if (expression is OqlPathExpression { Segments.Count: > 0 } path && aggregateInput &&
            path.Segments[0].Name == query.GroupAs && DocumentBindingScope.Resolve(local, query.GroupAs, out _) is null)
        { throw new DocumentPlanningException("CD01031", ErrorMessages.GroupArrayAggregateInput, expression.Location); }
        if (expression is OqlComprehensionExpression comprehension)
        {
            ValidateGroupArrayInput(comprehension.Collection, query, scopes, functions, aggregateInput, local);
            var inner = scopes.Enter(comprehension, comprehension.Variable, local);
            ValidateGroupArrayInput(comprehension.Projection, query, scopes, functions, aggregateInput, inner);
            if (comprehension.Predicate is not null) { ValidateGroupArrayInput(comprehension.Predicate, query, scopes, functions, aggregateInput, inner); }
        }
        else if (expression is OqlQuantifiedExpression quantified)
        {
            ValidateGroupArrayInput(quantified.Collection, query, scopes, functions, aggregateInput, local);
            ValidateGroupArrayInput(quantified.Predicate, query, scopes, functions, aggregateInput, scopes.Enter(quantified, quantified.Variable, local));
        }
        else if (expression is OqlCallExpression call)
        {
            foreach (var argument in call.Arguments) { ValidateGroupArrayInput(argument, query, scopes, functions, aggregateInput || functions.IsAggregate(call.Name), local); }
        }
        else if (expression is OqlArrayExpression array)
        { foreach (var element in array.Elements) { ValidateGroupArrayInput(element, query, scopes, functions, aggregateInput, local); } }
        else if (expression is OqlObjectExpression obj)
        { foreach (var member in obj.Members) { ValidateGroupArrayInput(member.Value, query, scopes, functions, aggregateInput, local); } }
        else if (expression is OqlBinaryExpression binary)
        {
            ValidateGroupArrayInput(binary.Left, query, scopes, functions, aggregateInput, local);
            ValidateGroupArrayInput(binary.Right, query, scopes, functions, aggregateInput, local);
        }
        else if (expression is OqlUnaryExpression unary) { ValidateGroupArrayInput(unary.Operand, query, scopes, functions, aggregateInput, local); }
        else { foreach (var child in PredicateChildren(expression)) { ValidateGroupArrayInput(child, query, scopes, functions, aggregateInput, local); } }
    }
}
