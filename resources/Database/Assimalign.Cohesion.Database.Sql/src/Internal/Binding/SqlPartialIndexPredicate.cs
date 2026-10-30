using System;
using System.Collections.Generic;
using System.Globalization;
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
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            SqlDefinitionBinder.PinCalls(expression, scope, index.PredicateFunctionCalls);
            Collect(expression);
        }
        catch (DatabaseException failure)
        { throw new SqlCatalogException("CS06059", ErrorMessages.PartialPredicateDependencies, failure); }
        if (!columns.SetEquals(index.PredicateColumns))
        { throw new SqlCatalogException("CS06059", ErrorMessages.PartialPredicateDependencies); }
        try
        {
            // Resolve every alternative before compiling: short circuits cannot hide a
            // missing or weakened registration, even for a write with no input rows.
            if (UnavailableRegistration(expression, scope) is { } unavailable)
            { return Unresolved(index, unavailable.Signature, unavailable.Reason); }
            Validate(expression, table, scope);
            SqlPersistedExpression.Bind(expression, scope);
            SqlPlanExecutor.ValidatePersistedPredicate(expression, table, scope);
            SqlPlanner.ValidateExpression(expression, scope);
            RequireBoolean(expression, scope);
            return scope.BindPredicate(expression);
        }
        catch (SqlPartialIndexRegistrationException failure)
        { return Unresolved(index, failure.Signature, failure.Message); }
        catch (SqlFunctionResultMismatchException mismatch)
        { return Unresolved(index, mismatch.Call.FunctionName, mismatch.Message); }
        catch (DatabaseException failure)
        {
            // As for CHECK, only a changed application contract explains an unresolved
            // definition. Invalid stored syntax, columns and dependency vectors are damage.
            throw new DatabaseException(string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.PartialPredicateBindingFailed, index.Name, index.Predicate, failure.Message,
                SqlPersistedExpression.DamagedCatalogHint),
                SqlExecutionException.DiagnosticsOf(failure, SqlExecutionException.DamagedDefinitionCode, discardLocations: true), failure);
        }

        void Collect(SqlExpression node)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            if (node is SqlColumnReferenceExpression column) { columns.Add(table.Columns[scope.ResolveColumn(column)].Name); }
            foreach (var child in SqlPlanner.Children(node)) { Collect(child); }
        }
    }

    private static SqlBoundExpression Unresolved(SqlCatalogIndex index, string signature, string reason)
    {
        var unresolved = new SqlUnresolvedDefinition(index.Name, signature, reason);
        return new SqlBoundFailure(() => throw SqlEvaluationException.UnregisteredFunction(unresolved));
    }

    private static (string Signature, string Reason)? UnavailableRegistration(SqlExpression expression, SqlExpressionEvaluator scope)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var child in SqlPlanner.Children(expression))
        { if (UnavailableRegistration(child, scope) is { } unavailable) { return unavailable; } }
        if (expression is SqlFunctionCallExpression call && !SqlStandardLibrary.IsEvaluatorForm(call.FunctionName))
        {
            try
            {
                var function = scope.ResolveFunction(call);
                if (function is null) { return (call.FunctionName, ErrorMessages.RegistrationMissingReason); }
                if (function is SqlExpressionFunction { Unresolved: { } reason }) { return (call.FunctionName, reason); }
                if (function.Kind != SqlFunctionKind.Scalar || function.Volatility != SqlFunctionVolatility.Immutable)
                { return (call.FunctionName, ErrorMessages.ImmutableRegistrationMissing); }
            }
            catch (DatabaseException failure) { return (call.FunctionName, failure.Message); }
        }
        if (expression is SqlCastExpression cast && scope.TryResolveCastTarget(cast) is { Check: SqlExpressionFunction { Unresolved: { } domainReason } } target)
        { return (target.Name, domainReason); }
        return null;
    }

    private static SqlExpressionEvaluator Scope(SqlCatalogTable table, Collation collation,
        SqlFunctionEnvironment functions, CancellationToken cancellationToken)
        => new(table.Columns, null, defaultCollation: collation, functions: functions,
            cancellationToken: cancellationToken, foldConstants: false, bindingContext: SqlBindingContext.IndexPredicate);

    internal static void ValidateConversions(SqlCast?[] conversions, SqlType target, SqlExpression operand,
        SqlExpressionEvaluator scope, Assimalign.Cohesion.Database.Language.Location? location)
    {
        // Dispatch uses the value's runtime type, not CASE's inferred type. Check the
        // union of all alternatives (including unreachable ones), or every slot if unknown.
        uint types = RuntimeTypes(operand, scope);
        for (int slot = 0; slot < conversions.Length; slot++)
        {
            if ((types & (1u << slot)) != 0 && conversions[slot] is { } conversion && !SqlStandardCasts.Contains(conversion))
            { throw new SqlPartialIndexRegistrationException(conversion.Source.Name + " -> " + conversion.Target.Name,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PartialPredicateCastUnavailable, conversion.Source.Name, conversion.Target.Name), location); }
        }
        if (target.Check is { Volatility: not SqlFunctionVolatility.Immutable })
        { throw new SqlPartialIndexRegistrationException(target.Name,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.PartialPredicateDomainUnavailable, target.Name), location); }
    }

    private static uint RuntimeTypes(SqlExpression expression, SqlExpressionEvaluator scope)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (expression)
        {
            case SqlColumnReferenceExpression:
            case SqlLiteralExpression:
                return 1u | (1u << (int)scope.StaticTypeOf(expression));
            case SqlCastExpression cast when scope.TryResolveCastTarget(cast) is { } target:
                return 1u | (1u << (int)target.Storage.Type);
            case SqlCollateExpression collate:
                return RuntimeTypes(collate.Operand, scope);
            case SqlCaseExpression conditional:
                uint alternatives = conditional.ElseResult is null ? 1u : RuntimeTypes(conditional.ElseResult, scope);
                foreach (var clause in conditional.WhenClauses) { alternatives |= RuntimeTypes(clause.Result, scope); }
                return alternatives;
            case SqlFunctionCallExpression call when SqlStandardLibrary.IsCoalesce(call.FunctionName) ||
                call.FunctionName.Equals("NULLIF", StringComparison.OrdinalIgnoreCase):
                uint arguments = 1u;
                foreach (var argument in call.Arguments) { arguments |= RuntimeTypes(argument, scope); }
                return arguments;
            case SqlFunctionCallExpression call when scope.ResolveFunction(call) is { ReturnType.IsPseudo: false } function:
                return 1u | (1u << (int)function.ReturnType.Storage.Type);
            default:
                return uint.MaxValue;
        }
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
            ValidateConversions(conversions, target, cast.Operand, scope, cast.Location);
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
