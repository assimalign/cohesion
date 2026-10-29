using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentPlanner
{
    // No iterators or optional state on the ordinary planning path.
    internal static bool HasSubquery(OqlExpression? expression, System.Threading.CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (expression is null) { return false; }
        if (expression is OqlSubqueryExpression) { return true; }
        if (expression is OqlSelectExpression query)
        {
            for (int index = 0; index < query.Projections.Count; index++) { if (HasSubquery(query.Projections[index].Expression)) { return true; } }
            if (HasSubquery(query.Predicate) || HasSubquery(query.Having)) { return true; }
            for (int index = 0; index < query.GroupBy.Count; index++) { if (HasSubquery(query.GroupBy[index])) { return true; } }
            for (int index = 0; index < query.OrderBy.Count; index++) { if (HasSubquery(query.OrderBy[index].Expression)) { return true; } }
            for (int index = 0; index < query.Unnest.Count; index++) { if (HasSubquery(query.Unnest[index].Collection)) { return true; } }
            return false;
        }
        if (expression is OqlUnaryExpression unary) { return HasSubquery(unary.Operand); }
        if (expression is OqlBinaryExpression binary) { return HasSubquery(binary.Left) || HasSubquery(binary.Right); }
        if (expression is OqlCallExpression call)
        { for (int index = 0; index < call.Arguments.Count; index++) { if (HasSubquery(call.Arguments[index])) { return true; } } return false; }
        if (expression is OqlArrayExpression array)
        { for (int index = 0; index < array.Elements.Count; index++) { if (HasSubquery(array.Elements[index])) { return true; } } return false; }
        if (expression is OqlObjectExpression obj)
        { for (int index = 0; index < obj.Members.Count; index++) { if (HasSubquery(obj.Members[index].Value)) { return true; } } return false; }
        if (expression is OqlInsertExpression insert)
        {
            if (HasSubquery(insert.Select) || HasSubquery(insert.Returning)) { return true; }
            for (int index = 0; index < insert.Values.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasSubquery(insert.Values[index].Key) || HasSubquery(insert.Values[index].Value)) { return true; }
            }
            return false;
        }
        if (expression is OqlUpsertExpression upsert)
        {
            if (HasSubquery(upsert.Select) || HasSubquery(upsert.Returning)) { return true; }
            for (int index = 0; index < upsert.Values.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasSubquery(upsert.Values[index].Key) || HasSubquery(upsert.Values[index].Value)) { return true; }
            }
            return false;
        }
        if (expression is OqlUpdateExpression update)
        {
            if (HasSubquery(update.Predicate) || HasSubquery(update.Returning)) { return true; }
            for (int index = 0; index < update.Assignments.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (HasSubquery(update.Assignments[index].Value)) { return true; }
            }
            return false;
        }
        if (expression is OqlDeleteExpression delete) { return HasSubquery(delete.Predicate) || HasSubquery(delete.Returning); }
        if (expression is OqlReturningExpression returning)
        {
            for (int index = 0; index < returning.Projections.Count; index++)
            { if (HasSubquery(returning.Projections[index].Expression)) { return true; } }
            return false;
        }
        if (expression is OqlComprehensionExpression comprehension) { return HasSubquery(comprehension.Collection) || HasSubquery(comprehension.Projection) || HasSubquery(comprehension.Predicate); }
        if (expression is OqlQuantifiedExpression quantified) { return HasSubquery(quantified.Collection) || HasSubquery(quantified.Predicate); }
        if (expression is OqlInExpression membership)
        {
            if (HasSubquery(membership.Operand) || HasSubquery(membership.Array)) { return true; }
            for (int index = 0; index < membership.Values.Count; index++) { if (HasSubquery(membership.Values[index])) { return true; } }
        }
        if (expression is OqlBetweenExpression between) { return HasSubquery(between.Operand) || HasSubquery(between.Lower) || HasSubquery(between.Upper); }
        if (expression is OqlLikeExpression like) { return HasSubquery(like.Operand) || HasSubquery(like.Pattern) || HasSubquery(like.Escape); }
        if (expression is OqlCaseExpression conditional)
        {
            if (HasSubquery(conditional.Operand) || HasSubquery(conditional.Otherwise)) { return true; }
            for (int index = 0; index < conditional.Branches.Count; index++)
            { if (HasSubquery(conditional.Branches[index].Key) || HasSubquery(conditional.Branches[index].Value)) { return true; } }
        }
        return false;
    }

    private DocumentNestedPlan PlanNested(OqlExpression expression)
    {
        if (expression is not OqlSelectExpression query)
        { throw new DocumentPlanningException("CK00001", ErrorMessages.DmlSubqueryDeferred, expression.Location); }
        var queries = new Dictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>(ReferenceEqualityComparer.Instance);
        VisitQuery(query, new Dictionary<string, bool>(StringComparer.Ordinal));
        return new DocumentNestedPlan(PlanCore(query), queries);

        void VisitQuery(OqlSelectExpression select, Dictionary<string, bool> outer)
        {
            // A true value marks an implicit collection binding: FROM of that name
            // starts another keyspace scan. An explicit outer alias is correlation.
            if (outer.TryGetValue(select.Collection, out bool keyspace) && !keyspace)
            { throw new DocumentPlanningException("CK00001", ErrorMessages.CorrelationDeferred, select.Location); }
            var source = new Dictionary<string, bool>(StringComparer.Ordinal) { [select.Alias ?? select.Collection] = select.Alias is null };
            foreach (var binding in select.Unnest)
            {
                Visit(binding.Collection, "UNNEST", source, outer);
                source[binding.Variable] = false;
            }
            Visit(select.Predicate, "WHERE", source, outer);
            foreach (var key in select.GroupBy) { Visit(key, "GROUP BY", source, outer); }
            var grouped = new Dictionary<string, bool>(source, StringComparer.Ordinal);
            foreach (var alias in select.GroupAliases) { if (alias is not null) { grouped[alias] = false; } }
            if (select.GroupAs is string group) { grouped[group] = false; }
            foreach (var projection in select.Projections) { Visit(projection.Expression, "projection", grouped, outer); }
            Visit(select.Having, "HAVING", grouped, outer);
            var ordered = new Dictionary<string, bool>(grouped, StringComparer.Ordinal);
            foreach (var projection in select.Projections)
            { if (projection.Alias is string name) { ordered[name] = false; } }
            foreach (var order in select.OrderBy)
            {
                // ORDER BY's standalone projection aliases take precedence over source
                // names, exactly as CreateLogicalPlan rewrites them before binding.
                var expression = order.Expression;
                if (expression is OqlPathExpression { Segments.Count: 1 } path &&
                    select.Projections.FirstOrDefault(projection => projection.Alias is not null && projection.Alias == path.Segments[0].Name) is { } projection)
                { expression = projection.Expression; }
                Visit(expression, "ORDER BY", ordered, outer);
            }
        }

        void Visit(OqlExpression? node, string owner, Dictionary<string, bool> local, Dictionary<string, bool> outer)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is null) { return; }
            if (node is OqlPathExpression path && path.Segments.Count > 0 && path.Segments[0].Name is string name && outer.ContainsKey(name) && !local.ContainsKey(name))
            { throw new DocumentPlanningException("CK00001", ErrorMessages.CorrelationDeferred, node.Location); }
            if (node is OqlSubqueryExpression subquery)
            {
                if (queries.ContainsKey(subquery)) { return; }
                var enclosing = new Dictionary<string, bool>(outer, StringComparer.Ordinal);
                foreach (var declaration in local) { enclosing[declaration.Key] = declaration.Value; }
                VisitQuery(subquery.Query, enclosing);
                queries.Add(subquery, new DocumentNestedQueryPlan(PlanCore(subquery.Query), CanCache(subquery.Query), owner));
                return;
            }
            if (node is OqlComprehensionExpression comprehension)
            {
                Visit(comprehension.Collection, owner, local, outer);
                var inner = new Dictionary<string, bool>(local, StringComparer.Ordinal) { [comprehension.Variable] = false };
                Visit(comprehension.Projection, owner, inner, outer);
                Visit(comprehension.Predicate, owner, inner, outer);
                return;
            }
            if (node is OqlQuantifiedExpression quantified)
            {
                Visit(quantified.Collection, owner, local, outer);
                Visit(quantified.Predicate, owner, new Dictionary<string, bool>(local, StringComparer.Ordinal) { [quantified.Variable] = false }, outer);
                return;
            }
            foreach (var child in NestedChildren(node)) { Visit(child, owner, local, outer); }
        }

        bool CanCache(OqlExpression node)
        {
            if (node is OqlCallExpression call && !DocumentFunctionCatalog.IsSpecial(call.Name))
            {
                var function = _functions.Find(call.Name, call.Arguments.Count == 1 && call.Arguments[0] is OqlStarExpression ? 0 : call.Arguments.Count);
                if (function is not (DocumentBuiltinScalarFunction or DocumentBuiltinAggregateFunction) || call.Name is "NOW_STR" or "NOW_MILLIS") { return false; }
            }
            return NestedChildren(node).All(CanCache);
        }
    }

    internal static IEnumerable<OqlExpression> NestedChildren(OqlExpression node)
    {
        switch (node)
        {
            case OqlSubqueryExpression subquery: yield return subquery.Query; break;
            case OqlSelectExpression query:
                foreach (var projection in query.Projections) { yield return projection.Expression; }
                if (query.Predicate is not null) { yield return query.Predicate; }
                foreach (var key in query.GroupBy) { yield return key; }
                if (query.Having is not null) { yield return query.Having; }
                foreach (var order in query.OrderBy) { yield return order.Expression; }
                foreach (var binding in query.Unnest) { yield return binding.Collection; }
                break;
            case OqlUnaryExpression unary: yield return unary.Operand; break;
            case OqlBinaryExpression binary: yield return binary.Left; yield return binary.Right; break;
            case OqlCallExpression call: foreach (var argument in call.Arguments) { yield return argument; } break;
            case OqlMetaExpression meta: foreach (var argument in meta.Arguments) { yield return argument; } break;
            case OqlArrayExpression array: foreach (var element in array.Elements) { yield return element; } break;
            case OqlObjectExpression obj: foreach (var member in obj.Members) { yield return member.Value; } break;
            default: foreach (var child in PredicateChildren(node)) { yield return child; } break;
        }
    }
    private static DatabasePlanDescription DescribeNested(DocumentNestedPlan plan, IEnumerable<Diagnostic>? diagnostics)
    {
        var nodes = new List<DatabasePlanNode>();
        AddQuery(plan.Root, null);
        return DatabasePlanDescription.Create(nodes, diagnostics);

        void AddQuery(DocumentStatementPlan queryPlan, int? parent)
        {
            int offset = nodes.Count;
            foreach (var node in DescribeNodes(queryPlan))
            {
                nodes.Add(new DatabasePlanNode { NodeId = offset + node.NodeId, ParentId = node.ParentId is int id ? offset + id : parent,
                    Operator = node.Operator, Target = node.Target, Index = node.Index, Detail = node.Detail, EstimatedRows = node.EstimatedRows });
            }
            var query = queryPlan is DocumentPlan select ? select.Logical.Query : ((DocumentSystemCollectionPlan)queryPlan).Logical.Query;
            AddExpressions(query, offset);
        }

        void AddExpressions(OqlExpression node, int parent)
        {
            if (node is OqlSubqueryExpression expression)
            {
                var nested = plan.Queries[expression];
                int id = nodes.Count;
                nodes.Add(new DatabasePlanNode { NodeId = id, ParentId = parent, Operator = "SubqueryExpression",
                    Detail = nested.Owner + "; " + (nested.CacheForStatement ? "cached_for_statement" : "per_evaluation") +
                        "; span=" + expression.Location?.Start + ".." + expression.Location?.End });
                AddQuery(nested.Plan, id);
                return;
            }
            foreach (var child in NestedChildren(node)) { AddExpressions(child, parent); }
        }
    }

    private static bool EquivalentNestedQuery(OqlSelectExpression left, OqlSelectExpression right)
    {
        if (ReferenceEquals(left, right)) { return true; }
        if (left.Collection != right.Collection || left.Alias != right.Alias || left.IsValue != right.IsValue ||
            left.IsDistinct != right.IsDistinct || left.GroupAs != right.GroupAs ||
            !left.GroupAliases.SequenceEqual(right.GroupAliases) || left.Projections.Count != right.Projections.Count ||
            left.OrderBy.Count != right.OrderBy.Count || left.Unnest.Count != right.Unnest.Count) { return false; }
        for (int index = 0; index < left.Projections.Count; index++)
        { if (left.Projections[index].Alias != right.Projections[index].Alias) { return false; } }
        for (int index = 0; index < left.OrderBy.Count; index++)
        { if (left.OrderBy[index].Descending != right.OrderBy[index].Descending || left.OrderBy[index].NullPlacement != right.OrderBy[index].NullPlacement) { return false; } }
        var variables = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < left.Unnest.Count; index++)
        {
            if (left.Unnest[index].Variable != right.Unnest[index].Variable || left.Unnest[index].IsLeft != right.Unnest[index].IsLeft) { return false; }
            variables.Add(left.Unnest[index].Variable);
        }
        if ((left.Predicate is null) != (right.Predicate is null) || (left.Having is null) != (right.Having is null) ||
            left.GroupBy.Count != right.GroupBy.Count) { return false; }
        var scopes = new DocumentBindingScopes();
        bool Same(OqlExpression? a, OqlExpression? b) => a is null || b is null ? a is null && b is null :
            EquivalentExpression(a, b, left.Alias, variables, scopes);
        return Same(left.Limit, right.Limit) && Same(left.Offset, right.Offset) &&
            NestedChildren(left).Zip(NestedChildren(right)).All(pair => Same(pair.First, pair.Second));
    }

}
