using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private static SqlPlan WrapSubqueries(SqlPlan input, IReadOnlyList<SqlSubqueryBinding> queries)
        => queries.Count == 0 ? input : queries.Any(query => query.IsCorrelated)
            ? new SqlCorrelatedSubqueryPlan(input, queries) : new SqlSubqueryPlan(input, queries);

    private SqlCorrelationScope CreateCorrelationScope(SqlSelectExpression? select, SqlTableReference? dml)
    {
        _queryScopes ??= new();
        IReadOnlyList<SqlCatalogColumn> columns;
        IReadOnlyList<SqlRelationBinding>? bindings = null;
        SqlRelationReference? reference;
        if (select is not null && select.From.Count > 0)
        {
            reference = select.From[0].Table;
            var source = ResolveRelation(reference, select);
            columns = source.Columns;
            if (select.From.Count > 1 || select.From[0].Joins.Count > 0 || source.Derived is not null)
            {
                bindings = BindJoin(select, source, out columns, out _, out _, metadataOnly: true);
            }
        }
        else if (select is not null)
        {
            reference = null;
            columns = [];
        }
        else
        {
            reference = dml!;
            columns = ResolveTable(dml!).Columns;
        }
        var local = new SqlExpressionEvaluator(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken, relation: reference);
        return new SqlCorrelationScope(NextCorrelationScope(), local, OuterScope) { Bindings = bindings };
    }

    private SqlCorrelationScope SubqueryScope(SqlCorrelationScope scope, SqlSelectExpression? select, SqlExpression source)
    {
        if (select is null) { return scope; }
        if (scope.Bindings is { } bindings)
        {
            int start = 0;
            foreach (var item in select.From)
            {
                int end = start + 1;
                foreach (var join in item.Joins)
                {
                    end++;
                    if (join.Condition is not null && ContainsNode(join.Condition, source))
                    {
                        return new SqlCorrelationScope(scope.Id, scope.Local.ForRelations(bindings.Skip(start).Take(end - start).ToArray()), scope.Parent);
                    }
                }
                start = end;
            }
        }
        bool grouped = select.GroupBy.Count > 0 || select.Having is not null || ProjectsOrOrdersByAggregate(select);
        if (!grouped || select.Where is not null && ContainsNode(select.Where, source) ||
            select.GroupBy.Any(key => ContainsNode(key, source))) { return scope; }
        // Aggregate operands run over source rows, before the grouped output frame exists.
        if (select.Columns.Any(column => UnderAggregate(column.Expression, source, false)) ||
            select.Having is not null && UnderAggregate(select.Having, source, false) ||
            select.OrderBy.Any(order => UnderAggregate(order.Expression, source, false))) { return scope; }
        return new SqlCorrelationScope(scope.Id, scope.Local, scope.Parent, select.GroupBy);
    }

    private bool UnderAggregate(SqlExpression expression, SqlExpression source, bool aggregate)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (ReferenceEquals(expression, source)) { return aggregate; }
        aggregate |= expression is SqlFunctionCallExpression call && IsAggregate(call);
        foreach (var child in Children(expression))
        {
            if (UnderAggregate(child, source, aggregate)) { return true; }
        }
        return false;
    }

    private static bool ContainsNode(SqlExpression expression, SqlExpression source)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        return ReferenceEquals(expression, source) || Children(expression).Any(child => ContainsNode(child, source));
    }
}
