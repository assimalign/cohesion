using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private void RejectOuterOnlyAggregates(SqlSelectExpression select)
    {
        foreach (var expression in SubquerySources(select))
        {
            if (expression is SqlFunctionCallExpression call && IsAggregate(call) &&
                AggregateLevel(call, new List<SqlSelectExpression?> { select }) is > 0 and var level)
            {
                // parse_agg.c:646-690,1327-1334: a subquery aggregate whose arguments
                // belong to an outer recursive level is an aggregate of that level.
                if (_commonTableState?.HasRecursion == true)
                {
                    var owner = OuterScope;
                    for (int index = 1; index < level && owner is not null; index++) { owner = owner.Parent; }
                    var recursive = owner?.Bindings?.FirstOrDefault(binding => binding.Source.Derived?.IsRecursiveReference == true)?.Source.Derived;
                    if (recursive is not null)
                    {
                        throw new SqlPlanningException("CS01061", string.Format(CultureInfo.CurrentCulture,
                            ErrorMessages.CteRecursiveAggregate, recursive.Name), call.Location);
                    }
                }
                throw new SqlUnsupportedQueryException(ErrorMessages.OuterOnlyAggregate, location: call.Location);
            }
        }
    }

    // PostgreSQL parse_agg.c:646-690,762,850: descend into query bodies, subtract
    // their depth, ignore their local variables, and take the lowest remaining level.
    // Inspect namespace names before grouped-row substitution or child planning:
    // arguments of an aggregate of a grouped level may read its ungrouped source
    // columns (parse_agg.c:1385-1405). This walk never plans a derived body or sublink.
    private int AggregateLevel(SqlFunctionCallExpression call, List<SqlSelectExpression?> scopes, SqlExpression? argument = null)
    {
        int minimum = int.MaxValue;
        bool unresolved = false;
        if (argument is not null) { Visit(argument, 0); }
        else { foreach (var child in Children(call)) { Visit(child, 0); } }
        return unresolved ? -1 : minimum == int.MaxValue ? 0 : minimum;

        void Visit(SqlExpression expression, int depth)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _cancellationToken.ThrowIfCancellationRequested();
            if (expression is SqlColumnReferenceExpression column)
            {
                int index = 0;
                foreach (var scope in scopes)
                {
                    var match = scope is null ? (Found: false, Stops: false) : AggregateNameMatch(scope, column);
                    if (match.Stops)
                    {
                        if (match.Found) { Include(index - depth); }
                        else { unresolved = true; }
                        return;
                    }
                    index++;
                }
                for (var outer = OuterScope; outer is not null; outer = outer.Parent, index++)
                {
                    try { outer.Local.ResolveLocalColumn(column); }
                    catch (SqlPlanningException exception) when (exception.UnresolvedColumn is not null && !exception.HasLocalQualifier) { continue; }
                    Include(index - depth);
                    return;
                }
                unresolved = true; // Ordinary binding owns unknown/ambiguous-column diagnostics.
                return;
            }
            if (expression is SqlFunctionCallExpression nested && IsAggregate(nested))
            {
                int level = AggregateLevel(nested, scopes);
                if (level < 0) { unresolved = true; }
                else { Include(level - depth); }
                // Its level already summarizes every dependency in its arguments. Walking
                // them again here would double the work for every nested aggregate.
                return;
            }
            var query = expression switch
            {
                SqlSubqueryExpression scalar => scalar.Select,
                SqlExistsExpression exists => exists.Subquery,
                SqlInExpression member => member.Subquery,
                SqlQuantifiedComparisonExpression quantified => quantified.Subquery,
                _ => null,
            };
            if (query is not null) { VisitQuery(query, depth + 1); }
            foreach (var child in Children(expression)) { Visit(child, depth); }
        }

        void Include(int level)
        {
            if (level >= 0) { minimum = Math.Min(minimum, level); }
        }

        void VisitQuery(SqlQueryBodyExpression body, int depth)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            _cancellationToken.ThrowIfCancellationRequested();
            if (body is SqlSetOperationExpression set)
            {
                VisitQuery(set.Left, depth);
                VisitQuery(set.Right, depth);
                return;
            }
            var query = (SqlSelectExpression)body;
            scopes.Insert(0, query);
            try
            {
                foreach (var root in SubqueryRoots(query)) { Visit(root, depth); }
                // Non-LATERAL bodies cannot see this query's siblings, but its lexical
                // level still counts when adjusting references to enclosing queries.
                scopes[0] = null;
                foreach (var relation in QueryRelations(query))
                {
                    if (relation is SqlDerivedTableReference derived) { VisitQuery(derived.Query, depth + 1); }
                }
            }
            finally { scopes.RemoveAt(0); }
        }
    }

    private (bool Found, bool Stops) AggregateNameMatch(SqlSelectExpression select, SqlColumnReferenceExpression column)
    {
        int matches = 0;
        bool qualifier = false;
        foreach (var item in select.From)
        {
            Match(item.Table, false);
            var leftNames = item.Joins.Any(join => join.IsNatural) ? AggregateColumnNames(item.Table, select).ToList() : null;
            foreach (var join in item.Joins)
            {
                if (leftNames is null)
                {
                    Match(join.Table, column.TableAlias is null && join.UsingColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase));
                    continue;
                }
                var rightNames = AggregateColumnNames(join.Table, select).ToArray();
                var names = NaturalUsingNames(join, leftNames, rightNames);
                Match(join.Table, column.TableAlias is null && names.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase));
                leftNames.AddRange(rightNames.Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)));
            }
        }
        return (matches == 1, matches > 0 || qualifier);

        void Match(SqlRelationReference relation, bool hidden)
        {
            if (relation is SqlJoinedTableReference group)
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
                Match(group.Item.Table, hidden);
                var leftNames = group.Item.Joins.Any(join => join.IsNatural) ? AggregateColumnNames(group.Item.Table, select).ToList() : null;
                foreach (var join in group.Item.Joins)
                {
                    if (leftNames is null)
                    {
                        Match(join.Table, hidden || column.TableAlias is null && join.UsingColumns.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase));
                        continue;
                    }
                    var rightNames = AggregateColumnNames(join.Table, select).ToArray();
                    var names = NaturalUsingNames(join, leftNames, rightNames);
                    Match(join.Table, hidden || column.TableAlias is null && names.Contains(column.ColumnName, StringComparer.OrdinalIgnoreCase));
                    leftNames.AddRange(rightNames.Where(name => !names.Contains(name, StringComparer.OrdinalIgnoreCase)));
                }
                return;
            }
            if (hidden || !SqlExpressionEvaluator.MatchesQualifier(relation, column)) { return; }
            qualifier |= column.TableAlias is not null;
            matches += AggregateColumnNames(relation, select).Count(name => string.Equals(name, column.ColumnName, StringComparison.OrdinalIgnoreCase));
        }
    }

    private IEnumerable<string> AggregateColumnNames(SqlRelationReference reference, SqlSelectExpression owner)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        _cancellationToken.ThrowIfCancellationRequested();
        if (reference is SqlJoinedTableReference group)
        {
            var groupNames = AggregateColumnNames(group.Item.Table, owner).ToList();
            foreach (var join in group.Item.Joins)
            {
                if (!join.IsNatural)
                {
                    groupNames.AddRange(AggregateColumnNames(join.Table, owner)
                        .Where(name => !join.UsingColumns.Contains(name, StringComparer.OrdinalIgnoreCase)));
                    continue;
                }
                var rightNames = AggregateColumnNames(join.Table, owner).ToArray();
                var usingNames = NaturalUsingNames(join, groupNames, rightNames);
                groupNames = usingNames.Concat(groupNames.Where(name => !usingNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    .Concat(rightNames.Where(name => !usingNames.Contains(name, StringComparer.OrdinalIgnoreCase))).ToList();
            }
            return groupNames;
        }
        if (reference is not SqlDerivedTableReference derived)
        {
            return ResolveRelation(reference, owner).Columns.Select(column => column.Name);
        }
        if (derived.Columns.Count > 0)
        {
            CheckTargetList(0, derived.Columns.Count, derived.Query.Location);
            return derived.Columns;
        }
        var body = derived.Query;
        while (body is SqlSetOperationExpression set) { body = set.Left; }
        var query = (SqlSelectExpression)body;
        CheckTargetList(0, query.Columns.Count, query.Location);
        var names = new List<string>();
        bool grouped = query.GroupBy.Count > 0 || query.Having is not null || ProjectsOrOrdersByAggregate(query);
        foreach (var projection in query.Columns)
        {
            if (projection.Expression is SqlStarExpression)
            {
                foreach (var item in query.From)
                {
                    if (!item.Joins.Any(join => join.IsNatural))
                    {
                        Append(AggregateColumnNames(item.Table, query));
                        foreach (var join in item.Joins)
                        {
                            Append(AggregateColumnNames(join.Table, query)
                                .Where(name => !join.UsingColumns.Contains(name, StringComparer.OrdinalIgnoreCase)));
                        }
                        continue;
                    }
                    var visible = AggregateColumnNames(item.Table, query).ToList();
                    foreach (var join in item.Joins)
                    {
                        var rightNames = AggregateColumnNames(join.Table, query).ToArray();
                        var usingNames = NaturalUsingNames(join, visible, rightNames);
                        visible = usingNames.Concat(visible.Where(name => !usingNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
                            .Concat(rightNames.Where(name => !usingNames.Contains(name, StringComparer.OrdinalIgnoreCase))).ToList();
                    }
                    Append(visible);
                }
            }
            else if (projection.Expression is SqlQualifiedStarExpression star)
            {
                foreach (var relation in QueryRelations(query).Where(relation =>
                    SqlExpressionEvaluator.MatchesQualifier(relation, star.TableName, star.SchemaName)))
                { Append(AggregateColumnNames(relation, query)); }
            }
            else
            {
                EnsureTargetCapacity(names, 1, projection.Expression.Location);
                names.Add(projection.Alias ?? (projection.Expression switch
                {
                    SqlColumnReferenceExpression column => column.ColumnName,
                    SqlFunctionCallExpression aggregate when grouped && IsAggregate(aggregate) => aggregate.FunctionName.ToLowerInvariant(),
                    _ => $"column{names.Count + 1}",
                }));
            }
        }
        return names;

        void Append(IEnumerable<string> additional)
        {
            foreach (string name in additional)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                EnsureTargetCapacity(names, 1, query.Location);
                names.Add(name);
            }
        }
    }

    private static IReadOnlyList<string> NaturalUsingNames(SqlJoinClause join, IEnumerable<string> left, IEnumerable<string> right)
        => join.IsNatural ? left.Where(name => right.Contains(name, StringComparer.OrdinalIgnoreCase)).ToArray() : join.UsingColumns;

    private static IEnumerable<SqlRelationReference> QueryRelations(SqlSelectExpression select)
    {
        foreach (var item in select.From)
        {
            if (item.Table is SqlJoinedTableReference group)
            { foreach (var relation in SourceRelations([group.Item])) { yield return relation; } }
            else { yield return item.Table; }
            foreach (var join in item.Joins)
            {
                if (join.Table is SqlJoinedTableReference right)
                { foreach (var relation in SourceRelations([right.Item])) { yield return relation; } }
                else { yield return join.Table; }
            }
        }
    }
}
