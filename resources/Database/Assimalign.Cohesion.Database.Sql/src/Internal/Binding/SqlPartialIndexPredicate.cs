using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Admission and binding for immutable, row-local index predicates.</summary>
internal static class SqlPartialIndexPredicate
{
    internal static SqlCatalogIndex Declare(SqlCatalogTable table, SqlCreateIndexExpression create,
        Collation collation, SqlFunctionEnvironment functions)
    {
        return Declare(table, create.IndexName, create.Columns, create.IsUnique, create.Predicate!, collation, functions);
    }

    internal static SqlCatalogIndex Declare(SqlCatalogTable table, string name, IReadOnlyList<string> keys, bool unique,
        SqlExpression expression, Collation collation, SqlFunctionEnvironment functions)
    {
        var scope = Scope(table, collation, functions, CancellationToken.None);
        Validate(expression, table, scope);
        SqlPlanner.ValidateExpression(expression, scope);
        RequireBoolean(expression, scope);
        _ = scope.BindPredicate(expression); // Includes implicit conversions in every alternative.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(expression);
        return new SqlCatalogIndex(table.ObjectId, name, keys, unique,
            predicate: SqlPersistedExpression.Canonicalize(expression, name),
            predicateColumns: columns.Order(StringComparer.Ordinal).ToArray(),
            predicateFunctionCalls: SqlDefinitionBinder.FunctionCalls(expression, scope));

        void Collect(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlColumnReferenceExpression column) { columns.Add(table.Columns[scope.ResolveColumn(column)].Name); }
            foreach (var child in SqlPlanner.Children(node)) { Collect(child); }
        }
    }

    internal static SqlBoundExpression Bind(SqlCatalogTable table, SqlCatalogIndex index,
        Collation collation, SqlFunctionEnvironment functions, CancellationToken cancellationToken)
    {
        var expression = SqlPersistedExpression.Load(index.Predicate!, index.Name);
        var scope = Scope(table, collation, functions, cancellationToken);
        SqlDefinitionBinder.PinCalls(expression, scope, index.PredicateFunctionCalls);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(expression);
        if (!columns.SetEquals(index.PredicateColumns))
        { throw new SqlCatalogException("CS06059", ErrorMessages.PartialPredicateDependencies); }
        // Resolve the entire stored contract before binding its executable tree. A
        // short-circuit must never hide a missing or weakened application registration.
        if (SqlDefinitionBinder.HasUnavailableCall(expression, scope, SqlFunctionVolatility.Immutable))
        {
            var unresolved = new SqlUnresolvedDefinition(index.Name, index.Predicate!, ErrorMessages.PartialPredicateUnavailable);
            return new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(unresolved));
        }
        Validate(expression, table, scope);
        SqlPersistedExpression.Bind(expression, scope);
        SqlPlanner.ValidateExpression(expression, scope);
        RequireBoolean(expression, scope);
        return scope.BindPredicate(expression);

        void Collect(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlColumnReferenceExpression column) { columns.Add(table.Columns[scope.ResolveColumn(column)].Name); }
            foreach (var child in SqlPlanner.Children(node)) { Collect(child); }
        }
    }

    private static SqlExpressionEvaluator Scope(SqlCatalogTable table, Collation collation,
        SqlFunctionEnvironment functions, CancellationToken cancellationToken)
        => new(table.Columns, null, defaultCollation: collation, functions: functions,
            cancellationToken: cancellationToken, foldConstants: false, bindingContext: SqlBindingContext.IndexPredicate);

    internal static void ValidateConversions(SqlCast?[] conversions, SqlType target, Assimalign.Cohesion.Database.Language.Location? location)
    {
        // The evaluator dispatches by the runtime value's type, which CASE/COALESCE
        // can change independently of the inferred source. Every dispatch slot must
        // be safe, including alternatives the current rows will never select.
        if (conversions.Any(conversion => conversion is not null && !SqlStandardCasts.Contains(conversion)) ||
            target.Check is { Volatility: not SqlFunctionVolatility.Immutable })
        { throw new SqlPlanningException("CS05091", ErrorMessages.PartialPredicateInvalid, location); }
    }

    private static void RequireBoolean(SqlExpression expression, SqlExpressionEvaluator scope)
    {
        if (scope.StaticTypeOf(expression) is not (DatabaseType.Boolean or DatabaseType.Null) &&
            !(expression is SqlLiteralExpression literal && SqlExpressionEvaluator.TryBooleanLiteral(literal, out _)))
        {
            throw SqlEvaluationException.BooleanRequired("INDEX WHERE", SqlType.NameOf(scope.StaticTypeOf(expression))).At(expression.Location);
        }
    }

    private static void Validate(SqlExpression expression, SqlCatalogTable table, SqlExpressionEvaluator scope)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlParameterExpression or SqlSubqueryExpression or SqlExistsExpression or
            SqlQuantifiedComparisonExpression or SqlWindowExpression or SqlNextValueExpression or SqlStarExpression or
            SqlInExpression { Values: null })
        {
            throw new SqlPlanningException("CS05091", ErrorMessages.PartialPredicateInvalid, expression.Location);
        }
        if (expression is SqlCastExpression cast)
        {
            if (cast.TargetType.Equals("REGCLASS", StringComparison.OrdinalIgnoreCase))
            { throw new SqlPlanningException("CS05091", ErrorMessages.PartialPredicateInvalid, cast.Location); }
            var target = SqlPlanner.ResolveCastTarget(cast, scope.Functions.Types);
            var conversions = scope.Functions.Types.ResolveCasts(scope.SourceDomain(cast.Operand), target);
            // Application casts have no volatility contract. Only standard conversions
            // and immutable domain checks can prove a persisted predicate immutable.
            ValidateConversions(conversions, target, cast.Location);
        }
        if (expression is SqlColumnReferenceExpression column)
        {
            if (column.TableAlias is not null && (!column.TableAlias.Equals(table.Name, StringComparison.OrdinalIgnoreCase) ||
                column.SchemaName is not null && !column.SchemaName.Equals(table.Schema, StringComparison.OrdinalIgnoreCase)))
            {
                throw new SqlPlanningException("CS01015", string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    ErrorMessages.MissingFromClauseEntry, column.TableAlias), column.Location);
            }
            scope.ResolveColumn(column);
        }
        if (expression is SqlFunctionCallExpression call && !SqlStandardLibrary.SpecialForms.Contains(call.FunctionName, StringComparer.OrdinalIgnoreCase))
        {
            if (SqlSequenceExecution.IsFunction(call.FunctionName) || scope.Functions.Catalog.IsAggregate(call.FunctionName))
            { throw new SqlPlanningException("CS05091", ErrorMessages.PartialPredicateInvalid, call.Location); }
            var function = scope.ResolveFunction(call);
            if (function is null)
            { throw new SqlPlanningException("CS01009", string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlFunctionMissing, call.FunctionName), call.Location); }
            if (function is not null && (function.Kind != SqlFunctionKind.Scalar || function.Volatility != SqlFunctionVolatility.Immutable))
            { throw new SqlPlanningException("CS05091", ErrorMessages.PartialPredicateInvalid, call.Location); }
        }
        foreach (var child in SqlPlanner.Children(expression)) { Validate(child, table, scope); }
    }
}
