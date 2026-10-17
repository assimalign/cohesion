using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlRelationSource BindDerivedTable(SqlDerivedTableReference derived)
    {
        if (_subqueryDepth >= 32)
        {
            throw new SqlUnsupportedQueryException(ErrorMessages.SubqueryNestingDepth, location: derived.Query.Location);
        }
        SqlPlan child;
        _subqueryDepth++;
        try
        {
            child = PlanSelect(derived.Query);
        }
        catch (DatabaseException exception) when (exception.Message.StartsWith("Unknown column '", StringComparison.Ordinal))
        {
            throw new SqlUnsupportedQueryException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DerivedTableOuterReference, exception.Message),
                exception, derived.Query.Location);
        }
        finally { _subqueryDepth--; }

        var output = QueryOutput(child);
        var projections = output.Projections;
        if (derived.Columns.Count > 0 && derived.Columns.Count != projections.Count)
        {
            throw new SqlPlanningException("COHSQLE013",
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.DerivedColumnCount, derived.Alias, projections.Count, derived.Columns.Count), derived.Query.Location);
        }
        var columns = new SqlCatalogColumn[projections.Count];
        SqlExpressionEvaluator? scope = null;
        for (int i = 0; i < columns.Length; i++)
        {
            var projection = projections[i];
            var inherited = projection.ColumnOrdinal is int ordinal ? output.Columns[ordinal] : null;
            if (inherited is null && (projection.Expression is SqlColumnReferenceExpression || projection.Value is SqlBoundColumn))
            {
                scope ??= new SqlExpressionEvaluator(output.Columns, _parameters, output.Bindings,
                    defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots,
                    functions: _functions, cancellationToken: _cancellationToken);
                int physical = projection.Expression is SqlColumnReferenceExpression reference
                    ? scope.ResolveColumn(reference) : ((SqlBoundColumn)projection.Value!).Ordinal;
                inherited = scope.MergedColumn(physical)?.Column ?? output.Columns[physical];
            }
            var type = inherited?.Type ?? (projection.Expression as SqlCastExpression)?.TargetTypeInfo
                ?? new DatabaseTypeInfo(projection.Type, projection.MaxLength);
            Collation? collation = null;
            if (type.Type is DatabaseType.String or DatabaseType.Character)
            {
                collation = projection.Collation.Resolve(null);
                // A pass-through default collation keeps its lower coercibility;
                // materialization must not turn it into a declared override.
                if (inherited is { Collation: null } && collation == _catalog.DefaultCollation) { collation = null; }
            }
            columns[i] = new SqlCatalogColumn(derived.Columns.Count == 0 ? projection.Name : derived.Columns[i],
                type, projection.IsNullable,
                collation: collation,
                domainName: inherited?.DomainName);
        }
        return new SqlRelationSource(new SqlDerivedRelation(derived.Alias!, child, columns));
    }

    /// <summary>The output contract shared by subqueries, derived tables and INSERT sources.</summary>
    private static (IReadOnlyList<SqlProjection> Projections, IReadOnlyList<SqlCatalogColumn> Columns,
        IReadOnlyList<SqlRelationBinding>? Bindings) QueryOutput(SqlPlan plan)
    {
        while (plan is SqlSubqueryPlan wrapper) { plan = wrapper.Input; }
        if (plan is not SqlRelationPlan relation)
        {
            throw new SqlPlanningException("COHSQLE013", ErrorMessages.QuerySourceRequired);
        }
        return (relation.OutputProjections, relation.OutputColumns, relation.OutputBindings);
    }
}
