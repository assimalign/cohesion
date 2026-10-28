using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal static class SqlViewQuery
{
    // One deterministic traversal for persisted calls, including CTEs, set operands,
    // derived bodies and scalar/set/EXISTS subqueries.
    internal static IEnumerable<SqlExpression> Nodes(SqlQueryBodyExpression query)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var cte in query.CommonTables)
        { foreach (var node in Nodes(cte.Body)) { yield return node; } }
        if (query is SqlSetOperationExpression set)
        {
            foreach (var node in Nodes(set.Left)) { yield return node; }
            foreach (var node in Nodes(set.Right)) { yield return node; }
        }
        if (query is SqlSelectExpression select)
        {
            foreach (var relation in Relations(select.From))
            {
                if (relation is SqlDerivedTableReference derived)
                { foreach (var node in Nodes(derived.Query)) { yield return node; } }
            }
        }
        foreach (var root in Expressions(query))
        { foreach (var node in Nodes(root)) { yield return node; } }
    }

    internal static IEnumerable<SqlRelationReference> Relations(IReadOnlyList<SqlFromItem> sources)
    {
        foreach (var item in sources)
        {
            foreach (var relation in RelationLeaves(item.Table)) { yield return relation; }
            foreach (var join in item.Joins)
            { foreach (var relation in RelationLeaves(join.Table)) { yield return relation; } }
        }
    }

    internal static IEnumerable<SqlRelationReference> RelationLeaves(SqlRelationReference reference)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (reference is not SqlJoinedTableReference group) { yield return reference; }
        else { foreach (var relation in Relations([group.Item])) { yield return relation; } }
    }

    internal static IEnumerable<SqlJoinClause> Joins(IReadOnlyList<SqlFromItem> sources)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var item in sources)
        {
            if (item.Table is SqlJoinedTableReference first)
            { foreach (var join in Joins([first.Item])) { yield return join; } }
            foreach (var join in item.Joins)
            {
                if (join.Table is SqlJoinedTableReference right)
                { foreach (var child in Joins([right.Item])) { yield return child; } }
                yield return join;
            }
        }
    }

    internal static IEnumerable<SqlExpression> Expressions(SqlQueryBodyExpression query)
    {
        if (query is SqlSelectExpression select)
        {
            foreach (var column in select.Columns) { yield return column.Expression; }
            foreach (var join in Joins(select.From)) { if (join.Condition is { } condition) { yield return condition; } }
            if (select.Where is { } where) { yield return where; }
            foreach (var key in select.GroupBy) { yield return key; }
            if (select.Having is { } having) { yield return having; }
            foreach (var window in select.Windows)
            {
                foreach (var key in window.Specification.PartitionBy) { yield return key; }
                foreach (var key in window.Specification.OrderBy) { yield return key.Expression; }
            }
        }
        foreach (var order in query.OrderBy) { yield return order.Expression; }
        if (query.Limit is { } limit) { yield return limit; }
        if (query.Offset is { } offset) { yield return offset; }
    }

    internal static IEnumerable<SqlExpression> Nodes(SqlExpression expression, bool queries = true)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        yield return expression;
        var query = expression switch
        {
            SqlSubqueryExpression scalar => scalar.Select,
            SqlExistsExpression exists => exists.Subquery,
            SqlInExpression member => member.Subquery,
            SqlQuantifiedComparisonExpression quantified => quantified.Subquery,
            _ => null,
        };
        if (queries && query is not null)
        { foreach (var node in Nodes(query)) { yield return node; } }
        foreach (var child in SqlPlanner.Children(expression))
        { foreach (var node in Nodes(child, queries)) { yield return node; } }
    }
}
