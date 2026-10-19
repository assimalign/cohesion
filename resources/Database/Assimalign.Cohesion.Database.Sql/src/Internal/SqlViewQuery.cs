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
            foreach (var relation in select.From.SelectMany(item => new[] { item.Table }.Concat(item.Joins.Select(join => join.Table))))
            {
                if (relation is SqlDerivedTableReference derived)
                { foreach (var node in Nodes(derived.Query)) { yield return node; } }
            }
        }
        foreach (var root in Expressions(query))
        { foreach (var node in Nodes(root)) { yield return node; } }
    }

    internal static IEnumerable<SqlExpression> Expressions(SqlQueryBodyExpression query)
    {
        if (query is SqlSelectExpression select)
        {
            foreach (var column in select.Columns) { yield return column.Expression; }
            foreach (var join in select.From.SelectMany(item => item.Joins)) { if (join.Condition is { } condition) { yield return condition; } }
            if (select.Where is { } where) { yield return where; }
            foreach (var key in select.GroupBy) { yield return key; }
            if (select.Having is { } having) { yield return having; }
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
            _ => null,
        };
        if (queries && query is not null)
        { foreach (var node in Nodes(query)) { yield return node; } }
        foreach (var child in SqlPlanner.Children(expression))
        { foreach (var node in Nodes(child, queries)) { yield return node; } }
    }
}
