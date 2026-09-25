using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private int _subqueryDepth;

    /// <summary>
    /// The result type of every subquery bound while planning this statement, keyed by
    /// its source node. Grouping needs a key's type before any row exists, and a subquery
    /// node carries no type of its own.
    /// </summary>
    private readonly Dictionary<SqlExpression, DatabaseType> _subqueryTypes = new();

    /// <summary>
    /// Binds each uncorrelated child independently, then binds the ordinary
    /// relational operators over typed slots. Scalar evaluation never runs a query.
    /// </summary>
    private SqlPlan PlanSubqueries(SqlSelectExpression select)
    {
        var queries = new List<SqlSubqueryBinding>();
        foreach (var expression in SubquerySources(select))
        {
            switch (expression)
            {
                case SqlSubqueryExpression scalar:
                    Bind(scalar, scalar.Select, SqlSubqueryKind.Scalar, false);
                    break;
                case SqlExistsExpression exists:
                    Bind(exists, exists.Subquery, SqlSubqueryKind.Exists, exists.IsNegated);
                    break;
                case SqlInExpression { Subquery: not null } member:
                    Bind(member, member.Subquery, SqlSubqueryKind.Set, false);
                    break;
            }
        }

        var input = PlanSelectCore(select);
        return queries.Count == 0 ? input : new SqlSubqueryPlan(input, queries);

        void Bind(SqlExpression source, SqlSelectExpression query, SqlSubqueryKind kind, bool negated)
        {
            if (_subqueryDepth >= 32)
            {
                throw new SqlUnsupportedQueryException("Subquery nesting exceeds the supported maximum depth of 32.");
            }
            SqlPlan child;
            _subqueryDepth++;
            try
            {
                child = PlanSelect(query);
            }
            catch (DatabaseException exception) when (exception.Message.StartsWith("Unknown column '", StringComparison.Ordinal))
            {
                throw new SqlUnsupportedQueryException($"Correlated subqueries are not supported; a subquery column must resolve in its local FROM/JOIN scope. {exception.Message}", exception);
            }
            finally
            {
                _subqueryDepth--;
            }
            var projections = SubqueryProjections(child);
            if (kind != SqlSubqueryKind.Exists && projections.Count != 1)
            {
                throw new DatabaseException($"{(kind == SqlSubqueryKind.Scalar ? "Scalar" : "IN")} subquery requires exactly one output column; found {projections.Count}.");
            }
            var type = kind == SqlSubqueryKind.Exists ? DatabaseType.Boolean : projections[0].Type;
            _subqueryTypes[source] = type;
            queries.Add(new SqlSubqueryBinding(child, source, type,
                kind == SqlSubqueryKind.Exists ? _catalog.DefaultCollation : SubqueryCollation(child),
                kind, negated));
        }
    }

    /// <summary>
    /// Yields every scalar expression reachable from a statement's clauses, without
    /// descending into a subquery's own select: a child scope binds independently, and
    /// its inner subqueries belong to its own plan.
    /// </summary>
    private static IEnumerable<SqlExpression> SubquerySources(SqlSelectExpression select)
    {
        foreach (var expression in Roots())
        {
            foreach (var node in Walk(expression))
            {
                yield return node;
            }
        }

        IEnumerable<SqlExpression> Roots()
        {
            foreach (var column in select.Columns)
            {
                yield return column.Expression;
            }
            foreach (var join in select.Joins)
            {
                if (join.Condition is not null)
                {
                    yield return join.Condition;
                }
            }
            foreach (var key in select.GroupBy)
            {
                yield return key;
            }
            foreach (var order in select.OrderBy)
            {
                yield return order.Expression;
            }
            if (select.Where is not null)
            {
                yield return select.Where;
            }
            if (select.Having is not null)
            {
                yield return select.Having;
            }
            if (select.Limit is not null)
            {
                yield return select.Limit;
            }
            if (select.Offset is not null)
            {
                yield return select.Offset;
            }
        }

        static IEnumerable<SqlExpression> Walk(SqlExpression expression)
        {
            yield return expression;

            // Children treats a subquery as opaque, which is what keeps a child scope's
            // own subqueries out of this statement's bindings.
            foreach (var child in Children(expression))
            {
                foreach (var node in Walk(child))
                {
                    yield return node;
                }
            }
        }
    }

    /// <summary>Reads output metadata without executing a child query, including empty results.</summary>
    private static IReadOnlyList<SqlProjection> SubqueryProjections(SqlPlan plan) => plan switch
    {
        SqlSubqueryPlan subquery => SubqueryProjections(subquery.Input),
        SqlSelectPlan select => select.Projections,
        SqlJoinPlan join => join.Projections,
        SqlGroupPlan group => group.Projections,
        SqlSystemViewPlan view => view.Projections,
        _ => throw new DatabaseException("A subquery requires a SELECT result."),
    };

    /// <summary>Preserves the child column's comparison collation across materialization.</summary>
    private Collation SubqueryCollation(SqlPlan plan)
    {
        if (plan is SqlSubqueryPlan subquery)
        {
            return SubqueryCollation(subquery.Input);
        }
        var columns = plan switch
        {
            SqlSelectPlan select => select.Table.Columns,
            SqlJoinPlan join => join.Columns,
            SqlGroupPlan group => group.SourceColumns,
            SqlSystemViewPlan view => view.View.Columns,
            _ => throw new DatabaseException("A subquery requires a SELECT result."),
        };
        var bindings = plan switch { SqlJoinPlan join => join.Bindings, SqlGroupPlan group => group.Bindings, _ => null };
        var projection = SubqueryProjections(plan)[0];
        var evaluator = new SqlExpressionEvaluator(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation);
        return projection.ColumnOrdinal is int ordinal ? evaluator.ResolveColumnCollation(ordinal)
            : evaluator.ResolveCollation(projection.Expression);
    }
}
