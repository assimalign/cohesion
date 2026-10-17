using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

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
    /// The slot every subquery bound while planning this statement fills, keyed by its source node
    /// and numbered from zero across every nesting level: the expression binder turns each such node
    /// into a read of its slot (<see cref="SqlBoundSubquery"/>, <see cref="SqlBoundInSubquery"/>).
    /// </summary>
    private readonly Dictionary<SqlExpression, SqlSubquerySlot> _subquerySlots = new();

    /// <summary>The number of subquery slots this statement has numbered so far.</summary>
    private int _subquerySlotCount;

    /// <summary>
    /// Binds each uncorrelated child independently, then binds the ordinary
    /// relational operators over typed slots. Scalar evaluation never runs a query.
    /// </summary>
    private SqlPlan PlanSubqueries(SqlSelectExpression select)
    {
        var queries = BindSubqueries(SubquerySources(select));
        var input = PlanSelectCore(select);
        return queries.Count == 0 ? input : new SqlSubqueryPlan(input, queries);
    }

    private IReadOnlyList<SqlSubqueryBinding> BindSubqueries(IEnumerable<SqlExpression> sources)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        var queries = new List<SqlSubqueryBinding>();
        foreach (var expression in sources)
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

        return queries;

        void Bind(SqlExpression source, SqlSelectExpression query, SqlSubqueryKind kind, bool negated)
        {
            if (_subqueryDepth >= 32)
            {
                throw new SqlUnsupportedQueryException("Subquery nesting exceeds the supported maximum depth of 32.", location: source.Location);
            }
            SqlPlan child;
            _subqueryDepth++;
            try
            {
                child = PlanSelect(query);
            }
            catch (DatabaseException exception) when (exception.Message.StartsWith("Unknown column '", StringComparison.Ordinal))
            {
                throw new SqlUnsupportedQueryException($"Correlated subqueries are not supported; a subquery column must resolve in its local FROM/JOIN scope. {exception.Message}", exception, source.Location);
            }
            finally
            {
                _subqueryDepth--;
            }
            var projections = SubqueryProjections(child);
            if (kind != SqlSubqueryKind.Exists && projections.Count != 1)
            {
                throw new SqlPlanningException("COHSQLE013", $"{(kind == SqlSubqueryKind.Scalar ? "Scalar" : "IN")} subquery requires exactly one output column; found {projections.Count}.", source.Location);
            }
            var type = kind == SqlSubqueryKind.Exists ? DatabaseType.Boolean : projections[0].Type;
            var collation = kind == SqlSubqueryKind.Exists ? _catalog.DefaultCollation : SubqueryCollation(child);
            var slot = new SqlSubquerySlot(_subquerySlotCount++, kind, collation, type);
            _subqueryTypes[source] = type;
            _subquerySlots[source] = slot;
            queries.Add(new SqlSubqueryBinding(child, slot.Id, type, collation, kind, negated));
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
            foreach (var node in WalkSubqueries(expression))
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
            if (select.From.Count > 1 || select.From.Count == 1 && select.From[0].Joins.Count > 0)
            {
                foreach (var condition in JoinConditions(select))
                {
                    yield return condition;
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

    }

    private static IEnumerable<SqlExpression> WalkSubqueries(SqlExpression expression)
    {
        // Each level is a nested iterator, so the stack grows with the tree (#1151).
        RuntimeHelpers.EnsureSufficientExecutionStack();
        yield return expression;

        // Children treats a subquery as opaque, which is what keeps a child scope's
        // own subqueries out of this statement's bindings.
        foreach (var child in Children(expression))
        {
            foreach (var node in WalkSubqueries(child))
            {
                yield return node;
            }
        }
    }

    // Keep the N-input walk's loop state out of Roots' iterator object. A stored-only
    // SELECT never creates this iterator and retains its original allocation footprint.
    private static IEnumerable<SqlExpression> JoinConditions(SqlSelectExpression select)
    {
        foreach (var item in select.From)
        {
            foreach (var join in item.Joins)
            {
                if (join.Condition is not null)
                {
                    yield return join.Condition;
                }
            }
        }
    }

    /// <summary>Reads output metadata without executing a child query, including empty results.</summary>
    private static IReadOnlyList<SqlProjection> SubqueryProjections(SqlPlan plan) => QueryOutput(plan).Projections;

    /// <summary>Preserves the child column's comparison collation across materialization.</summary>
    private static Collation SubqueryCollation(SqlPlan plan) => QueryOutput(plan).Projections[0].Collation.Resolve(null);
}
