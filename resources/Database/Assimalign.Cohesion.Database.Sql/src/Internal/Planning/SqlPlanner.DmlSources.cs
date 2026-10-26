using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private (SqlCatalogTable Table, SqlExpressionEvaluator Evaluator, SqlDmlSource? Source,
        IReadOnlyList<SqlSubqueryBinding> Queries) BindDmlSources(SqlTableReference target,
        IReadOnlyList<SqlFromItem> sources, SqlExpression? where, SqlQueryExpression statement)
    {
        if (sources.Count == 0)
        {
            var children = BindDmlSubqueries(where, target);
            var table = ResolveTable(target);
            return (table, DmlEvaluator(table.Columns, null, target), null, children);
        }

        var targetTable = ResolveTable(target);
        var first = new SqlRelationSource(targetTable);
        // Cache derived-source bindings across namespace construction and expression binding.
        _queryScopes ??= new();
        var bindings = BindJoinSources(sources, statement, first, out var columns, out _, out _,
            metadataOnly: true, target: target);
        var evaluator = DmlEvaluator(columns, bindings, target);
        int scopeId = NextCorrelationScope();
        var queries = new List<SqlSubqueryBinding>();
        int start = 1;
        foreach (var item in sources)
        {
            int end = start + 1;
            foreach (var join in item.Joins)
            {
                end++;
                if (join.Condition is not null)
                {
                    // An ON namespace contains only this comma item; the target is separate.
                    var local = evaluator.ForRelations(bindings.Skip(start).Take(end - start).ToArray());
                    queries.AddRange(BindSubqueries(WalkSubqueries(join.Condition), dml: target,
                        scope: new SqlCorrelationScope(scopeId, local, OuterScope)));
                }
            }
            start = end;
        }
        if (where is not null)
        {
            queries.AddRange(BindSubqueries(WalkSubqueries(where), dml: target,
                scope: new SqlCorrelationScope(scopeId, evaluator, OuterScope)));
        }
        bindings = BindJoinSources(sources, statement, first, out columns, out var steps, out var visibleOrdinals, target: target);
        evaluator = DmlEvaluator(columns, bindings, target);
        // Association slots retain every physical input column, including hidden USING inputs.
        var projections = new SqlProjection[columns.Count];
        for (int ordinal = 0; ordinal < columns.Count; ordinal++)
        {
            projections[ordinal] = PassThrough(columns[ordinal].Name, ordinal, columns, evaluator);
        }
        var associations = new SqlJoinPlan(bindings, columns, steps, projections, null, [], null, null, false);
        return (targetTable, evaluator, new SqlDmlSource(associations, 0, visibleOrdinals), queries);
    }

    private SqlExpressionEvaluator DmlEvaluator(IReadOnlyList<SqlCatalogColumn> columns,
        IReadOnlyList<SqlRelationBinding>? bindings, SqlTableReference target, bool foldConstants = true)
        => new(columns, _parameters, bindings, defaultCollation: _catalog.DefaultCollation,
            subquerySlots: _subquerySlots, functions: _functions, cancellationToken: _cancellationToken,
            outerScope: OuterScope, outerDependencies: OuterDependencies, relation: target, foldConstants: foldConstants);
}
