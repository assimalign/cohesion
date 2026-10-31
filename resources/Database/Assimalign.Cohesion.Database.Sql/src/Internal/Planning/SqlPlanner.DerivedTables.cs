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
    private SqlQueryScopeState? _queryScopes;

    private bool InDerivedBody => _queryScopes?.Scopes.Any(scope => scope.Derived is not null) ?? false;

    private SqlProjectionMetadata? CaptureProjectionMetadata(SqlCollationCandidate candidate, SqlCatalogColumn? source = null,
        SqlExpression? expression = null, SqlExpressionEvaluator? evaluator = null)
    {
        if (ViewDefinitionBinding is null && !_inferringTableOutput && !InDerivedBody && _commonTableState?.BindingOperands is null && !(_queryScopes?.SetOperandDepth > 0)) { return null; }
        SqlType? declared = expression is null ? null : UnwrapCollation(expression) switch
        {
            SqlCastExpression cast => SqlDefinitionBinder.ResolveType(cast.TargetType, _functions, cast.Location),
            SqlJsonFunctionExpression json => evaluator!.JsonReturning(json),
            SqlFunctionCallExpression call when !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName) => evaluator!.ResolveFunction(call)?.ReturnType,
            _ => null,
        };
        return new(candidate, source, expression is null ? null : evaluator!.OutputTypeInfo(expression),
            source?.DomainName ?? (declared is { IsDomain: true } ? declared.Name : expression is null ? null : evaluator!.OutputDomainName(expression)));
    }

    private SqlRelationSource BindDerivedTable(SqlDerivedTableReference derived, SqlSelectExpression? owner)
    {
        if (_subqueryDepth >= 32)
        {
            throw new SqlUnsupportedQueryException(ErrorMessages.SubqueryNestingDepth, location: derived.Query.Location);
        }
        var parentDependencies = OuterDependencies;
        var dependencies = new HashSet<int>();
        OuterDependencies = dependencies;
        SqlPlan child;
        (_queryScopes ??= new()).Scopes.Push((owner, null, derived));
        _subqueryDepth++;
        try
        {
            child = PlanQuery(derived.Query);
        }
        catch (SqlPlanningException exception) when (exception.UnresolvedColumn is not null && !exception.HasLocalQualifier)
        {
            var column = exception.UnresolvedColumn;
            // This level's FROM items need LATERAL. Enclosing query levels already resolve
            // through the lexical scope chain. PostgreSQL parse_relation.c:3759-3789.
            if (owner is not null && ReferenceBindsIn(owner, column))
            {
                throw new SqlUnsupportedQueryException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.DerivedTableOuterReference, exception.Message),
                    exception, column.Location);
            }
            throw;
        }
        finally { _subqueryDepth--; _queryScopes.Scopes.Pop(); OuterDependencies = parentDependencies; }
        parentDependencies?.UnionWith(dependencies);

        return BindDerivedOutput(child, derived.Alias!, derived.Columns, derived.Query, dependencies.Count > 0);
    }

    private SqlRelationSource BindDerivedOutput(SqlPlan child, string relationName, IReadOnlyList<string> names, SqlQueryBodyExpression query, bool isCorrelated = false)
    {
        var output = QueryOutput(child);
        var projections = output.Projections;
        if (names.Count > 0 && names.Count != projections.Count)
        {
            throw new SqlPlanningException("CS05013",
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.DerivedColumnCount, relationName, projections.Count, names.Count), query.Location);
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
                    functions: _functions, cancellationToken: _cancellationToken, foldConstants: FoldQueryConstants,
                    outerScope: OuterScope, outerDependencies: OuterDependencies);
                inherited = scope.ColumnMetadata(reference);
            }
            SqlType? declaredType = null;
            if (projection.Expression is { } projected)
            {
                var value = UnwrapCollation(projected);
                if (value is SqlCastExpression cast) { declaredType = SqlDefinitionBinder.ResolveType(cast.TargetType, _functions, cast.Location); }
                else if (value is SqlJsonFunctionExpression json) { declaredType = (scope ?? ScopelessEvaluator).JsonReturning(json); }
                else if (value is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
                {
                    scope ??= new SqlExpressionEvaluator(output.Columns, _parameters, output.Bindings,
                        defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots, functions: _functions,
                        cancellationToken: _cancellationToken, outerScope: OuterScope, outerDependencies: OuterDependencies, foldConstants: FoldQueryConstants);
                    if (scope.ResolveFunction(call)?.ReturnType is { IsPseudo: false } result) { declaredType = result; }
                }
            }
            DatabaseTypeInfo? inferredType = null;
            if (declaredType is null && projection.Expression is { } resultExpression && projection.Metadata?.TypeInfo is null && inherited is null)
            {
                scope ??= new SqlExpressionEvaluator(output.Columns, _parameters, output.Bindings,
                    defaultCollation: _catalog.DefaultCollation, subquerySlots: _subquerySlots, functions: _functions,
                    cancellationToken: _cancellationToken, outerScope: OuterScope, outerDependencies: OuterDependencies, foldConstants: FoldQueryConstants);
                inferredType = scope.OutputTypeInfo(resultExpression);
            }
            var type = projection.Metadata?.TypeInfo ?? inherited?.Type ?? declaredType?.Storage
                ?? inferredType ?? (projection.Expression as SqlCastExpression)?.TargetTypeInfo ?? new DatabaseTypeInfo(projection.Type, projection.MaxLength);
            // PostgreSQL parse_relation.c:1715-1721 takes metadata from the projected
            // expression. Default strength must not become a declared column override.
            var candidate = type.Type is DatabaseType.String or DatabaseType.Character
                ? (projection.Metadata?.Collation ?? default).Resolve(null) : default;
            string name = names.Count == 0 ? projection.Name : names[i];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new SqlPlanningException("CS05013", ErrorMessages.DerivedColumnName, projection.Expression?.Location ?? query.Location);
            }
            columns[i] = new SqlCatalogColumn(name,
                type, projection.IsNullable,
                collation: candidate.Priority >= 2 ? candidate.Collation : null,
                domainName: projection.Metadata?.DomainName ?? inherited?.DomainName ?? (declaredType is { IsDomain: true } ? declaredType.Name : null));
        }
        return new SqlRelationSource(new SqlDerivedRelation(relationName, child, columns, isCorrelated));
    }

    private IReadOnlyList<SqlSubqueryBinding> BindDmlSubqueries(SqlExpression? where, SqlTableReference table)
    {
        return BindSubqueries(where is null ? [] : WalkSubqueries(where), dml: table);
    }

    private bool ReferenceBindsIn(SqlSelectExpression select, SqlColumnReferenceExpression column)
        => select.From.Any(item => ReferenceBindsTo(item.Table, column, select) ||
            item.Joins.Any(join => ReferenceBindsTo(join.Table, column, select)));

    private bool ReferenceBindsTo(SqlRelationReference? reference, SqlColumnReferenceExpression column, SqlSelectExpression? owner = null)
    {
        if (reference is SqlJoinedTableReference group)
        {
            return SourceRelations([group.Item]).Any(child => ReferenceBindsTo(child, column, owner));
        }
        if (reference is null || (_queryScopes?.Scopes.Any(scope => ReferenceEquals(scope.Derived, reference)) ?? false) || !SqlExpressionEvaluator.MatchesQualifier(reference, column))
        {
            return false;
        }
        try
        {
            var source = ResolveRelation(reference, owner);
            if (source.Derived is { IsCommonTable: true } && column.SchemaName is not null) { return false; }
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
        while (true)
        {
            if (plan is SqlSubqueryPlan wrapper) { plan = wrapper.Input; }
            else if (plan is SqlCorrelatedSubqueryPlan correlated) { plan = correlated.Input; }
            else if (plan is SqlWithPlan with) { plan = with.Input; }
            else { break; }
        }
        if (plan is not SqlRelationPlan relation)
        {
            throw new SqlPlanningException("CS05013", ErrorMessages.QuerySourceRequired);
        }
        return (relation.OutputProjections, relation.OutputColumns, relation.OutputBindings);
    }
}
