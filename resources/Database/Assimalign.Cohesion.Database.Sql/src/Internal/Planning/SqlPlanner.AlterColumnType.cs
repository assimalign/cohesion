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
            previous.DefaultLiteral, ResolveColumnCollation(change.Column, type.Storage), type.IsDomain ? type.Name : null,
            previous.DefaultFunctionCalls, previous.Identity);
        return PlanColumnRewrite(table, column, type, change.UsingExpression, reference);
    }

    private static bool RequiresColumnRewrite(SqlCatalogColumn previous, SqlCatalogColumn column)
        => previous.Type.Type != column.Type.Type || previous.Type.Scale != column.Type.Scale ||
            !string.Equals(previous.DomainName, column.DomainName, StringComparison.OrdinalIgnoreCase) ||
            previous.Collation != column.Collation;

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

        string? text = column.DefaultLiteral;
        if (recastDefault && text is not null)
        {
            // USING never transforms the default (tablecmds.c:15269-15304). Capture
            // the old literal's SQL identity before testing the assignment pathway;
            // otherwise a TEXT default '7' would incorrectly become unknown input.
            var defaultExpression = SqlPersistedExpression.Load(text, "ALTER COLUMN DEFAULT");
            var defaultScope = new SqlExpressionEvaluator([], null, defaultCollation: _catalog.DefaultCollation,
                functions: _functions, cancellationToken: _cancellationToken, foldConstants: false);
            SqlDefinitionBinder.PinCalls(defaultExpression, defaultScope, column.DefaultFunctionCalls);
            ValidateExpression(defaultExpression, defaultScope);
            bool literal = SqlPersistedExpression.IsDefaultLiteral(defaultExpression);
            var defaultSource = literal ? previous.Type.Type : defaultScope.StaticTypeOf(defaultExpression);
            var defaultDomain = literal ? previousDomain : defaultScope.SourceDomain(defaultExpression);
            var defaultCast = BindAlterAssignment(new SqlBoundConstant(null), defaultSource, defaultDomain, target, column.Name,
                defaultExpression is SqlLiteralExpression { LiteralType: SqlLiteralType.Null });
            // Keep only storage identities in these casts. The outer DEFAULT binder
            // applies target modifiers as assignment, including on future volatile calls.
            if (literal)
            {
                var sourceType = previousDomain ?? SqlType.ForStorage(previous.Type.Type == DatabaseType.Character
                    ? previous.Type : new DatabaseTypeInfo(previous.Type.Type));
                text = "CAST(" + text + " AS " + AlterCastType(sourceType) + ")";
            }
            var defaultTarget = target.IsDomain ? target : SqlType.ForStorage(new DatabaseTypeInfo(target.Storage.Type));
            if (target.Storage.Type == DatabaseType.Character)
            {
                // Bare CHAR reparses as CHAR(1). For standard character assignment,
                // persist its unbounded TEXT input and let the column binder pad/check.
                // An application CHAR conversion cannot be replaced by this TEXT route.
                var characterCast = defaultCast.Casts[(int)defaultSource];
                var textCast = _functions.Types.ResolveCast(defaultDomain, defaultSource, SqlType.Text);
                if (characterCast is not null && !SqlStandardCasts.Contains(characterCast) ||
                    textCast is not null && !SqlStandardCasts.Contains(textCast))
                { throw new SqlPlanningException("CK00001", ErrorMessages.AlterTypeCharacterDefaultCast); }
                defaultTarget = SqlType.Text;
            }
            text = "CAST(" + text + " AS " + AlterCastType(defaultTarget) + ")";
        }
        var replacement = new SqlCatalogColumn(column.Name, column.Type, column.IsNullable, text, column.Collation,
            column.DomainName, column.DefaultFunctionCalls, column.Identity);
        return new SqlAlterColumnPlan(table, replacement, AllowDefaultChange: true, conversion);
    }

    private static string AlterCastType(SqlType type)
        => type.IsDomain ? "\"" + type.Name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : type.Name;

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
