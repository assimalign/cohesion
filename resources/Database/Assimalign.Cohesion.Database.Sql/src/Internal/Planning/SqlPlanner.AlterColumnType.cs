using System;
using System.Globalization;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanner
{
    private SqlAlterColumnPlan PlanAlterColumnType(SqlTableReference reference, SqlAlterColumnTypeAction change)
    {
        var table = ResolveTable(reference);
        var previous = table.Columns[FindColumnOrdinal(table, change.Column.ColumnName)];
        var type = ResolveColumnType(change.Column);
        // PostgreSQL tablecmds.c:15263: the target declaration selects its collation.
        var column = new SqlCatalogColumn(previous.Name, type.Storage, previous.IsNullable,
            previous.DefaultLiteral ?? type.DefaultLiteral, ResolveColumnCollation(change.Column, type.Storage), type.IsDomain ? type.Name : null,
            previous.DefaultLiteral is null ? DefaultCalls(type.DefaultLiteral) : previous.DefaultFunctionCalls,
            previous.Identity, previous.MissingValueLiteral);
        return PlanColumnRewrite(table, column, type, change.UsingExpression, reference);
    }

    private bool RequiresColumnRewrite(SqlCatalogColumn previous, SqlCatalogColumn column)
        => previous.Type.Type != column.Type.Type || previous.Type.Scale != column.Type.Scale ||
            !string.Equals(previous.DomainName, column.DomainName, StringComparison.OrdinalIgnoreCase) ||
            (previous.Collation ?? _catalog.DefaultCollation).Id != (column.Collation ?? _catalog.DefaultCollation).Id;

    private SqlAlterColumnPlan PlanColumnRewrite(SqlCatalogTable table, SqlCatalogColumn column, SqlType target,
        SqlExpression? expression, SqlTableReference reference, bool recastDefault = true)
    {
        int ordinal = FindColumnOrdinal(table, column.Name);
        var previous = table.Columns[ordinal];
        var scope = new SqlExpressionEvaluator(table.Columns, _parameters, defaultCollation: _catalog.DefaultCollation,
            functions: _functions, cancellationToken: _cancellationToken,
            relation: reference);
        DatabaseType source = previous.Type.Type;
        SqlBoundExpression operand = new SqlBoundColumn(ordinal);
        SqlType? previousDomain = previous.DomainName is { } name && _functions.Types.TryGetDomain(name, out var registeredDomain)
            ? registeredDomain : null;
        SqlType? domain = previousDomain;
        if (expression is not null)
        {
            ValidateAlterUsing(expression);
            ValidateFunctionCalls(expression);
            ValidateExpression(expression, scope);
            source = scope.StaticTypeOf(expression);
            operand = scope.Bind(expression);
            domain = scope.SourceDomain(expression);
        }
        // PostgreSQL tablecmds.c:14949-14998 binds the original row, then requires
        // COERCION_ASSIGNMENT for both an implicit column value and a USING result.
        var conversion = BindAlterAssignment(operand, source, domain, target, column.Name,
            expression is SqlLiteralExpression { LiteralType: SqlLiteralType.String or SqlLiteralType.Null });

        string? text = recastDefault && previous.DefaultLiteral is not null
            ? RecastColumnDefault(previous, target, previousDomain) : column.DefaultLiteral;
        var replacement = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable, text, column.Collation,
            column.DomainName, text is not null && SqlPersistedExpression.IsDefaultLiteral(SqlPersistedExpression.Load(text, "ALTER COLUMN DEFAULT"))
                ? [] : column.DefaultFunctionCalls, column.Identity, column.MissingValueLiteral);
        // The decision 49a path validates facets without copying rows or rebuilding
        // indexes. CHAR width changes can repad values and still need the copier.
        bool compatible = expression is null && !RequiresColumnRewrite(previous, replacement) &&
            !(column.Type.Type == DatabaseType.Character && column.Type.MaxLength != previous.Type.MaxLength);
        return new SqlAlterColumnPlan(table, replacement, AllowDefaultChange: true,
            compatible ? null : conversion, TypeChange: true);
    }

    private string RecastColumnDefault(SqlCatalogColumn previous, SqlType target, SqlType? previousDomain)
    {
        // Strip the column's implicit coercion by binding its original expression.
        // Only unknown strings acquired the old column identity; numeric/Boolean
        // literals already have their own identity (tablecmds.c:15269-15304).
        var expression = SqlPersistedExpression.Load(previous.DefaultLiteral!, "ALTER COLUMN DEFAULT");
        var scope = new SqlExpressionEvaluator([], null, defaultCollation: _catalog.DefaultCollation,
            functions: _functions, cancellationToken: _cancellationToken, foldConstants: false);
        SqlDefinitionBinder.PinCalls(expression, scope, previous.DefaultFunctionCalls);
        ValidateExpression(expression, scope);
        bool resolvedString = expression is SqlLiteralExpression { LiteralType: SqlLiteralType.String };
        var source = resolvedString ? previous.Type.Type : scope.StaticTypeOf(expression);
        var domain = resolvedString ? previousDomain : scope.SourceDomain(expression);
        var operand = resolvedString
            ? SqlPlanExecutor.BindDefaultExpression(previous, "ALTER COLUMN DEFAULT", _functions,
                _catalog.DefaultCollation, _cancellationToken, declaring: true)
            : scope.Bind(expression);
        var conversion = BindAlterAssignment(operand, source, domain, target, previous.Name,
            expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Null });
        if (target.Storage.Type == DatabaseType.Character)
        {
            var characterCast = conversion.Casts[(int)source];
            var textCast = _functions.Types.ResolveCast(domain, source, SqlType.Text);
            if (characterCast is not null && !SqlStandardCasts.Contains(characterCast) ||
                textCast is not null && !SqlStandardCasts.Contains(textCast))
            { throw new SqlPlanningException("CK00001", ErrorMessages.AlterTypeCharacterDefaultCast); }
        }
        if (SqlPlanExecutor.DefaultVolatility(expression, scope) != SqlFunctionVolatility.Immutable)
        {
            // The DEFAULT binder supplies assignment coercion on every evaluation.
            // Persisting the original tree avoids CAST growth and explicit truncation.
            return previous.DefaultLiteral!;
        }
        var evaluator = SqlExpressionEvaluator.ForExecution(null, _cancellationToken);
        var value = evaluator.Evaluate(conversion, []);
        SqlPlanExecutor.EnsureFiniteDefault(value);
        return SqlPlanExecutor.FrozenDefaultLiteral(value, target.Storage.Type);
    }

    private void ValidateAlterUsing(SqlExpression expression)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (expression is SqlFunctionCallExpression call && _functions.Catalog.IsAggregate(call.FunctionName))
        {
            throw new SqlPlanningException("CK00001", ErrorMessages.AlterTypeUsingScalarOnly, expression.Location);
        }
        foreach (var child in Children(expression)) { ValidateAlterUsing(child); }
    }

    private SqlBoundCast BindAlterAssignment(SqlBoundExpression operand, DatabaseType source, SqlType? domain,
        SqlType target, string column, bool unknownLiteral)
    {
        var casts = _functions.Types.ResolveCasts(domain, target);
        var cast = casts[(int)source];
        if (source != DatabaseType.Null && !unknownLiteral &&
            (cast?.Context == SqlCastContext.Explicit || cast is null && source != target.Storage.Type))
        {
            throw new SqlPlanningException("CS02064", string.Format(CultureInfo.CurrentCulture,
                ErrorMessages.AlterTypeAssignmentRequired, column, SqlType.NameOf(source), target.Name));
        }
        // Domain validation runs with all converted columns through the shared write
        // checker, rather than invoking the target domain twice for each row.
        return new SqlBoundCast(operand, target.Name, SqlType.ForStorage(target.Storage), casts,
            _functions.Database, _catalog.DefaultCollation, sourceType: source, assignment: true);
    }
}
