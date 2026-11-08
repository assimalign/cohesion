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
        if (expression is OqlSetOperationExpression compound)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (HasSubquery(compound.Left, cancellationToken) || HasSubquery(compound.Right, cancellationToken)) { return true; }
            for (int index = 0; index < compound.OrderBy.Count; index++)
            { if (HasSubquery(compound.OrderBy[index].Expression, cancellationToken)) { return true; } }
            return false;
        }
        if (expression is OqlSubqueryExpression) { return true; }
        if (expression is OqlMergeExpression merge)
        {
            foreach (var child in NestedChildren(merge)) { if (HasSubquery(child, cancellationToken)) { return true; } }
            return false;
        }
        if (expression is OqlSelectExpression query)
        {
            if (query.Source is not null || query.Bindings != query.Unnest) { return true; }
            for (int index = 0; index < query.Projections.Count; index++) { if (HasSubquery(query.Projections[index].Expression)) { return true; } }
            if (HasSubquery(query.Predicate) || HasSubquery(query.Having)) { return true; }
            for (int index = 0; index < query.GroupBy.Count; index++) { if (HasSubquery(query.GroupBy[index])) { return true; } }
            for (int index = 0; index < query.OrderBy.Count; index++) { if (HasSubquery(query.OrderBy[index].Expression)) { return true; } }
            for (int index = 0; index < query.Bindings.Count; index++) { if (HasSubquery(query.Bindings[index].Expression)) { return true; } }
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
        var queryOuterNames = new Dictionary<OqlQueryExpression, IReadOnlySet<string>>(ReferenceEqualityComparer.Instance);
        var queries = new Dictionary<OqlSubqueryExpression, DocumentNestedQueryPlan>(ReferenceEqualityComparer.Instance);
        var empty = new Dictionary<string, bool>(StringComparer.Ordinal);
        var free = new HashSet<string>(StringComparer.Ordinal);
        string? unboundValuesName = null;
        bool valuesAliasExplicit = false;
        int subqueryDepth = 0;
        string? unavailableMergeTarget = null;
        string? unavailableMergeReturningSource = null;
        if (expression is OqlQueryExpression query) { VisitQuery(query, empty, free); }
        else if (expression is OqlMergeExpression merge)
        {
            ValidateMergeDeclaration(merge);
            string target = DocumentSourceName.Of(merge.Collection, merge.Alias);
            string source = MergeSourceName(merge.Source);
            VisitQuery(MergeSourceQuery(merge.Source), empty, free);
            // Preserve OQ-K1 provenance: implicit names do not redirect an inner FROM
            // keyspace, while explicit correlation aliases can supply expression rows.
            var both = new Dictionary<string, bool>(StringComparer.Ordinal)
            { [source] = merge.Source.Alias is null, [target] = merge.Alias is null };
            var sourceOnly = new Dictionary<string, bool>(StringComparer.Ordinal) { [source] = merge.Source.Alias is null };
            unavailableMergeTarget = merge.IsLookup ? target : null;
            Visit(merge.On, "MERGE ON", merge.IsLookup ? sourceOnly : both, empty, free);
            unavailableMergeTarget = null;
            foreach (var child in MergeActionExpressions(merge))
            {
                if (child is OqlReturningExpression)
                {
                    unavailableMergeReturningSource = source;
                    Visit(child, "RETURNING", new Dictionary<string, bool>(StringComparer.Ordinal) { [target] = merge.Alias is null }, empty, free);
                    unavailableMergeReturningSource = null;
                }
                else { Visit(child, "MERGE action", both, empty, free); }
            }
        }
        else
        {
            string? name = expression switch
            {
                OqlUpdateExpression update => DocumentSourceName.Of(update.Collection, update.Alias),
                OqlDeleteExpression delete => DocumentSourceName.Of(delete.Collection, delete.Alias),
                OqlInsertExpression insert => DocumentSourceName.Of(insert.Collection, insert.Alias),
                OqlUpsertExpression upsert => DocumentSourceName.Of(upsert.Collection, upsert.Alias),
                _ => null,
            };
            var local = new Dictionary<string, bool>(StringComparer.Ordinal);
            valuesAliasExplicit = expression is OqlInsertExpression { Alias: not null } or OqlUpsertExpression { Alias: not null };
            if (name is not null)
            {
                local[name] = expression switch
                {
                    OqlUpdateExpression update => update.Alias is null,
                    OqlDeleteExpression delete => delete.Alias is null,
                    OqlInsertExpression insert => insert.Alias is null,
                    OqlUpsertExpression upsert => upsert.Alias is null,
                    _ => false,
                };
            }
            foreach (var child in NestedChildren(expression))
            {
                if (child is OqlQueryExpression select) { VisitQuery(select, empty, free); }
                else
                {
                    bool values = expression is OqlInsertExpression or OqlUpsertExpression && child is not OqlReturningExpression;
                    unboundValuesName = values ? name : null;
                    Visit(child, child is OqlReturningExpression ? "RETURNING" : "DML", values ? empty : local, empty, free);
                    unboundValuesName = null;
                }
            }
        }
        return new DocumentNestedPlan(PlanCore(expression, queries, queryOuterNames: queryOuterNames), queries);

        OqlQueryExpression VisitQuery(OqlQueryExpression queryExpression, Dictionary<string, bool> outer, HashSet<string> free)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            _cancellationToken.ThrowIfCancellationRequested();
            if (queryExpression is OqlSetOperationExpression compound)
            {
                var leftFree = new HashSet<string>(StringComparer.Ordinal);
                var rightFree = new HashSet<string>(StringComparer.Ordinal);
                var left = VisitQuery(compound.Left, outer, leftFree);
                var right = VisitQuery(compound.Right, outer, rightFree);
                free.UnionWith(leftFree);
                free.UnionWith(rightFree);
                return ReferenceEquals(left, compound.Left) && ReferenceEquals(right, compound.Right) ? compound :
                    new OqlSetOperationExpression(left, right, compound.Operation, compound.IsAll, compound.OrderBy,
                        compound.Limit, compound.Offset, compound.Location);
            }
            var select = (OqlSelectExpression)queryExpression;
            if (unboundValuesName is not null && valuesAliasExplicit && select.Source is null &&
                select.Collection == unboundValuesName && !outer.ContainsKey(select.Collection))
            { throw new DocumentPlanningException("CD01036", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                ErrorMessages.ValuesDestinationUnbound, unboundValuesName), select.Location); }
            bool capturesSource = select.Source is null && outer.TryGetValue(select.Collection, out bool keyspace) && !keyspace;
            var source = new Dictionary<string, bool>(StringComparer.Ordinal)
            { [DocumentSourceName.Of(select)] = select.IsAliasImplicit && !capturesSource };
            var joinNames = select.Bindings == select.Unnest ? null :
                new HashSet<string>(select.Bindings.Select(binding => binding.Variable), StringComparer.Ordinal);
            if (capturesSource)
            { free.Add(select.Collection); }
            Visit(select.Source, "FROM", new Dictionary<string, bool>(StringComparer.Ordinal), outer, free);
            foreach (var binding in select.Bindings)
            {
                Visit(binding.Expression, binding is OqlJoinExpression ? "JOIN" : "UNNEST", source, outer, free,
                    keyNames: binding is OqlJoinExpression ? joinNames : null);
                source[binding.Variable] = binding is OqlJoinExpression { Alias: null } or OqlUnnestExpression { Alias: null };
            }
            Visit(select.Predicate, "WHERE", source, outer, free);
            foreach (var key in select.GroupBy) { Visit(key, "GROUP BY", source, outer, free); }
            var grouped = new Dictionary<string, bool>(source, StringComparer.Ordinal);
            foreach (var alias in select.GroupAliases) { if (alias is not null) { grouped[alias] = false; } }
            if (select.GroupAs is string group) { grouped[group] = false; }
            foreach (var projection in select.Projections) { Visit(projection.Expression, "projection", grouped, outer, free); }
            Visit(select.Having, "HAVING", grouped, outer, free);
            VisitOrdering(select, grouped,
                (scope, name) => new Dictionary<string, bool>(scope, StringComparer.Ordinal) { [name] = false },
                (expression, scope, nested) => { Visit(expression, "ORDER BY", scope, outer, free, nested); return true; });
            var normalized = !capturesSource ? select : new OqlSelectExpression("", DocumentSourceName.Of(select), select.Projections,
                select.Predicate, select.GroupBy, select.Having, select.OrderBy, select.IsDistinct, select.IsValue,
                select.Limit, select.Offset, select.Location, select.Unnest, select.GroupAliases, select.GroupAs,
                new OqlPathExpression([new OqlPathSegment(select.Collection, null)], select.Location),
                select.Bindings == select.Unnest ? null : select.Bindings);
            queryOuterNames[normalized] = free;
            return normalized;
        }

        void Visit(OqlExpression? node, string owner, Dictionary<string, bool> local, Dictionary<string, bool> outer, HashSet<string> free,
            Dictionary<string, bool>? nestedOrder = null, IReadOnlySet<string>? keyNames = null)
        {
            _cancellationToken.ThrowIfCancellationRequested();
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is null) { return; }
            if (node is OqlPathExpression path && path.Segments.Count > 0 && path.Segments[0].Name is string name)
            {
                if (name == unavailableMergeTarget && !local.ContainsKey(name) && !outer.ContainsKey(name))
                { throw new DocumentPlanningException("CD01040", ErrorMessages.MergeBindingInvalid, node.Location); }
                if (name == unavailableMergeReturningSource && !local.ContainsKey(name) && !outer.ContainsKey(name))
                { throw new DocumentPlanningException("CK00001", ErrorMessages.MergeReturningSourceUnsupported, node.Location); }
                if (subqueryDepth > 0 && name == unboundValuesName && !local.ContainsKey(name) && !outer.ContainsKey(name))
                { throw new DocumentPlanningException("CD01036", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.ValuesDestinationUnbound, name), node.Location); }
                if (outer.ContainsKey(name) && !local.ContainsKey(name))
                { free.Add(name); }
                if (owner == "JOIN" && keyNames?.Contains(name) == true && !local.ContainsKey(name) && !outer.ContainsKey(name))
                { throw new DocumentPlanningException("CD01035", ErrorMessages.JoinBindingInvalid, node.Location); }
                if (owner == "FROM" && !local.ContainsKey(name) && !outer.ContainsKey(name))
                { throw new DocumentPlanningException("CD01034", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.FromVariableUnbound, name), node.Location); }
            }
            if (node is OqlSubqueryExpression subquery)
            {
                if (queries.ContainsKey(subquery)) { return; }
                var enclosing = new Dictionary<string, bool>(outer, StringComparer.Ordinal);
                foreach (var declaration in nestedOrder ?? local) { enclosing[declaration.Key] = declaration.Value; }
                var outerNames = new HashSet<string>(StringComparer.Ordinal);
                subqueryDepth++;
                OqlQueryExpression normalized;
                try { normalized = VisitQuery(subquery.Query, enclosing, outerNames); }
                finally { subqueryDepth--; }
                foreach (var dependency in outerNames)
                { if (!(nestedOrder ?? local).ContainsKey(dependency)) { free.Add(dependency); } }
                queries.Add(subquery, new DocumentNestedQueryPlan(PlanCore(normalized, queries, outerNames, queryOuterNames),
                    outerNames.Count == 0 && CanCache(normalized), owner, outerNames));
                return;
            }
            if (node is OqlComprehensionExpression comprehension)
            {
                Visit(comprehension.Collection, owner, local, outer, free, nestedOrder, keyNames);
                var inner = new Dictionary<string, bool>(local, StringComparer.Ordinal) { [comprehension.Variable] = false };
                var orderedInner = nestedOrder is null ? null : new Dictionary<string, bool>(nestedOrder, StringComparer.Ordinal) { [comprehension.Variable] = false };
                Visit(comprehension.Projection, owner, inner, outer, free, orderedInner, keyNames);
                Visit(comprehension.Predicate, owner, inner, outer, free, orderedInner, keyNames);
                return;
            }
            if (node is OqlQuantifiedExpression quantified)
            {
                Visit(quantified.Collection, owner, local, outer, free, nestedOrder, keyNames);
                Visit(quantified.Predicate, owner, new Dictionary<string, bool>(local, StringComparer.Ordinal) { [quantified.Variable] = false }, outer, free,
                    nestedOrder is null ? null : new Dictionary<string, bool>(nestedOrder, StringComparer.Ordinal) { [quantified.Variable] = false }, keyNames);
                return;
            }
            foreach (var child in NestedChildren(node)) { Visit(child, owner, local, outer, free, nestedOrder, keyNames); }
        }

        bool CanCache(OqlExpression node)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
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
            case OqlMergeExpression merge:
                if (merge.Source.Expression is not null) { yield return merge.Source.Expression; }
                yield return merge.On;
                foreach (var child in MergeActionExpressions(merge)) { yield return child; }
                break;
            case OqlInsertExpression insert:
                if (insert.Select is not null) { yield return insert.Select; }
                foreach (var pair in insert.Values) { yield return pair.Key; yield return pair.Value; }
                if (insert.Returning is not null) { yield return insert.Returning; }
                break;
            case OqlUpsertExpression upsert:
                if (upsert.Select is not null) { yield return upsert.Select; }
                foreach (var pair in upsert.Values) { yield return pair.Key; yield return pair.Value; }
                if (upsert.Returning is not null) { yield return upsert.Returning; }
                break;
            case OqlUpdateExpression update:
                if (update.Predicate is not null) { yield return update.Predicate; }
                foreach (var assignment in update.Assignments) { yield return assignment.Value; }
                if (update.Returning is not null) { yield return update.Returning; }
                break;
            case OqlDeleteExpression delete:
                if (delete.Predicate is not null) { yield return delete.Predicate; }
                if (delete.Returning is not null) { yield return delete.Returning; }
                break;
            case OqlReturningExpression returning:
                foreach (var projection in returning.Projections) { yield return projection.Expression; }
                break;
            case OqlSubqueryExpression subquery: yield return subquery.Query; break;
            case OqlSetOperationExpression compound:
                yield return compound.Left;
                yield return compound.Right;
                foreach (var order in compound.OrderBy) { yield return order.Expression; }
                if (compound.Limit is not null) { yield return compound.Limit; }
                if (compound.Offset is not null) { yield return compound.Offset; }
                break;
            case OqlSelectExpression query:
                if (query.Source is not null) { yield return query.Source; }
                foreach (var projection in query.Projections) { yield return projection.Expression; }
                if (query.Predicate is not null) { yield return query.Predicate; }
                foreach (var key in query.GroupBy) { yield return key; }
                if (query.Having is not null) { yield return query.Having; }
                foreach (var order in query.OrderBy) { yield return order.Expression; }
                foreach (var binding in query.Bindings) { yield return binding.Expression; }
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
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            int offset = nodes.Count;
            foreach (var node in queryPlan is DocumentSetOperationPlan ? DescribeNodes(queryPlan).Take(1) : DescribeNodes(queryPlan))
            {
                nodes.Add(new DatabasePlanNode { NodeId = offset + node.NodeId, ParentId = node.ParentId is int id ? offset + id : parent,
                    Operator = node.Operator, Target = node.Target, Index = node.Index, Detail = node.Detail, EstimatedRows = node.EstimatedRows });
            }
            if (queryPlan is DocumentSetOperationPlan compound)
            {
                AddQuery(compound.Left, offset);
                AddQuery(compound.Right, offset);
                return;
            }
            OqlExpression query = queryPlan switch
            {
                DocumentMergePlan merge => merge.Statement,
                DocumentUpdatePlan update => update.Statement,
                DocumentDeletePlan delete => delete.Statement,
                DocumentInsertPlan insert => insert.Statement,
                DocumentInsertSelectPlan insert => insert.Statement,
                DocumentUpsertPlan upsert => upsert.Statement,
                _ => QueryLogical(queryPlan).Query,
            };
            if (queryPlan is DocumentMergePlan mergePlan)
            {
                AddQuery(mergePlan.Source, offset);
                foreach (var child in MergeActionExpressions(mergePlan.Statement).Prepend(mergePlan.Statement.On))
                { AddExpressions(child, offset); }
                return;
            }
            var writeSource = queryPlan switch
            {
                DocumentInsertSelectPlan insert => insert.Source is DocumentFromPlan or DocumentJoinPlan or DocumentSetOperationPlan ? insert.Source : null,
                DocumentUpsertPlan upsert => upsert.Source is DocumentFromPlan or DocumentJoinPlan or DocumentSetOperationPlan ? upsert.Source : null,
                _ => null,
            };
            if (writeSource is not null) { AddQuery(writeSource, offset); }
            var inputPlan = queryPlan is DocumentJoinPlan joined ? joined.Input : queryPlan;
            if (inputPlan is DocumentFromPlan { Input: not null } source)
            { AddQuery(source.Input, nodes.Skip(offset).First(node => node.Operator == "DerivedSource").NodeId); }
            foreach (var child in NestedChildren(query))
            {
                if (writeSource is not null && child is OqlQueryExpression) { continue; }
                if (!ReferenceEquals(child, (query as OqlSelectExpression)?.Source)) { AddExpressions(child, offset); }
                else if (inputPlan is DocumentFromPlan { Input: null })
                { AddExpressions(child, nodes.Skip(offset).First(node => node.Operator == "ExpressionSource").NodeId); }
            }
        }

        void AddExpressions(OqlExpression node, int parent)
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is OqlSubqueryExpression expression)
            {
                var nested = plan.Queries[expression];
                int id = nodes.Count;
                nodes.Add(new DatabasePlanNode { NodeId = id, ParentId = parent, Operator = "SubqueryExpression",
                    Detail = nested.Owner + "; " + (nested.CacheForStatement ? "cached_for_statement" : "per_evaluation") +
                        "; correlation=" + (nested.OuterNames.Count == 0 ? "none" : string.Join(",", nested.OuterNames.Order(StringComparer.Ordinal))) +
                        "; span=" + expression.Location?.Start + ".." + expression.Location?.End });
                AddQuery(nested.Plan, id);
                return;
            }
            foreach (var child in NestedChildren(node)) { AddExpressions(child, parent); }
        }
    }

    private static bool EquivalentNestedQuery(OqlQueryExpression leftQuery, OqlQueryExpression rightQuery)
    {
        System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        if (ReferenceEquals(leftQuery, rightQuery)) { return true; }
        if (leftQuery is OqlSetOperationExpression leftSet)
        {
            if (rightQuery is not OqlSetOperationExpression rightSet || leftSet.Operation != rightSet.Operation || leftSet.IsAll != rightSet.IsAll ||
                leftSet.OrderBy.Count != rightSet.OrderBy.Count || !EquivalentNestedQuery(leftSet.Left, rightSet.Left) || !EquivalentNestedQuery(leftSet.Right, rightSet.Right)) { return false; }
            // Compound tails bind only result members. Compare them with an empty query scope.
            var scope = new OqlSelectExpression("", null, [], null, [], null, [], false, false);
            var bindings = new DocumentBindingScopes();
            bool SameTail(OqlExpression? x, OqlExpression? y) => x is null || y is null ? x is null && y is null :
                EquivalentExpression(x, y, scope, null, bindings);
            return SameTail(leftSet.Limit, rightSet.Limit) && SameTail(leftSet.Offset, rightSet.Offset) && leftSet.OrderBy.Zip(rightSet.OrderBy).All(pair =>
                pair.First.Descending == pair.Second.Descending && pair.First.NullPlacement == pair.Second.NullPlacement &&
                SameTail(pair.First.Expression, pair.Second.Expression));
        }
        if (leftQuery is not OqlSelectExpression left || rightQuery is not OqlSelectExpression right) { return false; }
        if (left.Collection != right.Collection || left.Alias != right.Alias || left.IsAliasImplicit != right.IsAliasImplicit || left.IsValue != right.IsValue ||
            left.IsDistinct != right.IsDistinct || left.GroupAs != right.GroupAs ||
            !left.GroupAliases.SequenceEqual(right.GroupAliases) || left.Projections.Count != right.Projections.Count ||
            left.OrderBy.Count != right.OrderBy.Count || left.Bindings.Count != right.Bindings.Count) { return false; }
        for (int index = 0; index < left.Projections.Count; index++)
        { if (left.Projections[index].Alias != right.Projections[index].Alias) { return false; } }
        for (int index = 0; index < left.OrderBy.Count; index++)
        { if (left.OrderBy[index].Descending != right.OrderBy[index].Descending || left.OrderBy[index].NullPlacement != right.OrderBy[index].NullPlacement) { return false; } }
        var variables = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < left.Bindings.Count; index++)
        {
            if (left.Bindings[index].Variable != right.Bindings[index].Variable || left.Bindings[index].IsLeft != right.Bindings[index].IsLeft) { return false; }
            if (left.Bindings[index] is OqlJoinExpression a && (right.Bindings[index] is not OqlJoinExpression b || a.Collection != b.Collection || a.Alias != b.Alias || a.IsNest != b.IsNest) ||
                left.Bindings[index] is OqlUnnestExpression unnest && (right.Bindings[index] is not OqlUnnestExpression other || unnest.Alias != other.Alias)) { return false; }
            variables.Add(left.Bindings[index].Variable);
        }
        if ((left.Predicate is null) != (right.Predicate is null) || (left.Having is null) != (right.Having is null) ||
            left.GroupBy.Count != right.GroupBy.Count) { return false; }
        var scopes = new DocumentBindingScopes();
        bool Same(OqlExpression? a, OqlExpression? b) => a is null || b is null ? a is null && b is null :
            EquivalentExpression(a, b, left, variables, scopes);
        return Same(left.Limit, right.Limit) && Same(left.Offset, right.Offset) &&
            NestedChildren(left).Zip(NestedChildren(right)).All(pair => Same(pair.First, pair.Second));
    }

}
