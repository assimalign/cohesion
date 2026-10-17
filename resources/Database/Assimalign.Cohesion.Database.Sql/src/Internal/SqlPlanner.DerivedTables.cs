using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private Stack<(SqlSelectExpression? Select, SqlTableReference? Dml, SqlDerivedTableReference? Derived)>? _queryScopes;

    private bool InDerivedBody => _queryScopes?.Any(scope => scope.Derived is not null) ?? false;

    private SqlProjectionMetadata? CaptureProjectionMetadata(SqlCollationCandidate candidate, SqlCatalogColumn? source = null)
        => InDerivedBody ? new(candidate, source) : null;

    private SqlRelationSource BindDerivedTable(SqlDerivedTableReference derived, SqlSelectExpression? owner)
    {
        if (_subqueryDepth >= 32)
        {
            throw new SqlUnsupportedQueryException(ErrorMessages.SubqueryNestingDepth, location: derived.Query.Location);
        }
        SqlPlan child;
        (_queryScopes ??= new()).Push((owner, null, derived));
        _subqueryDepth++;
        try
        {
            child = PlanSelect(derived.Query);
        }
        catch (SqlPlanningException exception) when (exception.UnresolvedColumn is not null && !exception.HasLocalQualifier)
        {
            var column = exception.UnresolvedColumn;
            // This level's FROM items need LATERAL; enclosing query levels are ordinary
            // correlation, deferred to part (a). PostgreSQL parse_relation.c:3759-3789.
            if (owner is not null && ReferenceBindsIn(owner, column))
            {
                throw new SqlUnsupportedQueryException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DerivedTableOuterReference, exception.Message),
                    exception, column.Location);
            }
            if (_queryScopes.Skip(1).ToArray().Any(scope => ReferenceBindsInScope(scope.Select, scope.Dml, column)))
            {
                throw new SqlUnsupportedQueryException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.CorrelatedSubquery, exception.Message),
                    exception, column.Location);
            }
            throw;
        }
        finally { _subqueryDepth--; _queryScopes.Pop(); }

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
            var inherited = projection.Metadata?.SourceColumn ?? (projection.ColumnOrdinal is int ordinal ? output.Columns[ordinal] : null);
            if (inherited is null && projection.Expression is { } expression &&
                UnwrapCollation(expression) is SqlColumnReferenceExpression reference)
            {
                scope ??= new SqlExpressionEvaluator(output.Columns, _parameters, output.Bindings,
                    defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots,
                    functions: _functions, cancellationToken: _cancellationToken);
                inherited = scope.ColumnMetadata(reference);
            }
            var type = inherited?.Type ?? (projection.Expression as SqlCastExpression)?.TargetTypeInfo
                ?? new DatabaseTypeInfo(projection.Type, projection.MaxLength);
            // PostgreSQL parse_relation.c:1715-1721 takes metadata from the projected
            // expression. Default strength must not become a declared column override.
            var candidate = type.Type is DatabaseType.String or DatabaseType.Character
                ? (projection.Metadata?.Collation ?? default).Resolve(null) : default;
            string name = derived.Columns.Count == 0 ? projection.Name : derived.Columns[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SqlPlanningException("COHSQLE013", ErrorMessages.DerivedColumnName, projection.Expression?.Location ?? derived.Query.Location);
            }
            columns[i] = new SqlCatalogColumn(name,
                type, projection.IsNullable,
                collation: candidate.Priority >= 2 ? candidate.Collation : null,
                domainName: inherited?.DomainName);
        }
        return new SqlRelationSource(new SqlDerivedRelation(derived.Alias!, child, columns));
    }

    private IReadOnlyList<SqlSubqueryBinding> BindDmlSubqueries(SqlExpression? where, SqlTableReference table)
    {
        return BindSubqueries(where is null ? [] : WalkSubqueries(where), dml: table);
    }

    private bool ReferenceBindsInScope(SqlSelectExpression? select, SqlTableReference? dml, SqlColumnReferenceExpression column)
        => select is not null && ReferenceBindsIn(select, column) || ReferenceBindsTo(dml, column);

    private bool ReferenceBindsIn(SqlSelectExpression select, SqlColumnReferenceExpression column)
        => select.From.Any(item => ReferenceBindsTo(item.Table, column, select) ||
            item.Joins.Any(join => ReferenceBindsTo(join.Table, column, select)));

    private bool ReferenceBindsTo(SqlRelationReference? reference, SqlColumnReferenceExpression column, SqlSelectExpression? owner = null)
    {
        if (reference is null || (_queryScopes?.Any(scope => ReferenceEquals(scope.Derived, reference)) ?? false) || !SqlExpressionEvaluator.MatchesQualifier(reference, column))
        {
            return false;
        }
        try
        {
            var source = ResolveRelation(reference, owner);
            var scope = new SqlExpressionEvaluator(source.Columns, _parameters, relation: reference);
            scope.ResolveColumn(column);
            return true;
        }
        // A diagnostic probe cannot establish a binding through an invalid sibling.
        // Its own planning will report that failure if this statement reaches it.
        catch (SqlPlanningException) { return false; }
        catch (SqlUnsupportedQueryException) { return false; }
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
