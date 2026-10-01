using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The engine's single path for SQL expression text the catalog persists: a <c>CHECK</c>
/// predicate and a column <c>DEFAULT</c> today, and every later persisted expression (expression
/// defaults, view queries). A definition is stored as the canonical text
/// <see cref="SqlExpressionRenderer"/> renders from its parsed tree — never the text the user
/// wrote — and is read back through <see cref="Load"/>, once per table version.
/// </summary>
/// <remarks>
/// Canonical text uses a fixed, minimal spelling of each construct, so how a definition was
/// written, and any leniency an older parser had for that spelling, never reaches storage.
/// <see cref="Canonicalize"/> proves before anything is stored that the text parses back to the
/// declared tree; a definition that would not round-trip fails its DDL instead of the database's
/// next open. The rule is documented in the Sql and Sql.Catalog designs and the dialect.
/// </remarks>
internal static class SqlPersistedExpression
{
    // Persisted text is parsed as the predicate of a carrier query over this table, then
    // required to be exactly that predicate (no other clause may ride along).
    private const string carrierTable = "__persisted";

    /// <summary>
    /// What a persisted definition that does not load means, and what to do about it. Canonical
    /// text always reloads, so such a definition was not written by this engine's DDL.
    /// </summary>
    internal const string DamagedCatalogHint =
        "The catalog is damaged or was written by an incompatible engine build; restore the database from a backup.";

    /// <summary>
    /// Renders the canonical text to persist for a parsed expression and proves it reloads to
    /// the same tree.
    /// </summary>
    /// <param name="expression">The validated expression to persist.</param>
    /// <param name="subject">Names the definition, for example <c>CHECK constraint 'ck' on table 'dbo.t'</c>.</param>
    /// <returns>The canonical text.</returns>
    /// <exception cref="DatabaseException">The expression has no canonical text that reproduces it.</exception>
    internal static string Canonicalize(SqlExpression expression, string subject)
    {
        ArgumentNullException.ThrowIfNull(expression);

        string text;
        try
        {
            text = SqlExpressionRenderer.Render(expression);
        }
        catch (NotSupportedException exception)
        {
            throw new DatabaseException($"{subject} cannot be stored: {exception.Message}", exception);
        }

        if (!TryParse(text, out var reloaded, out string? problem) || !AreEquivalent(expression, reloaded))
        {
            throw new DatabaseException(
                $"{subject} cannot be stored: its canonical text '{text}' does not reproduce the declared expression" +
                $"{(problem is null ? string.Empty : $" ({problem})")}. This is an engine defect; nothing was changed.");
        }

        return text;
    }

    /// <summary>Parses persisted canonical text back into its expression tree.</summary>
    /// <param name="text">The persisted text.</param>
    /// <param name="subject">Names the definition for the error message.</param>
    /// <returns>The expression tree.</returns>
    /// <exception cref="DatabaseException">The text is not one valid SQL expression.</exception>
    internal static SqlExpression Load(string text, string subject)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryParse(text, out var expression, out string? problem))
        {
            throw new DatabaseException(
                $"{subject} cannot be loaded: its persisted definition '{text}' is not a valid SQL expression ({problem}). {DamagedCatalogHint}");
        }

        return expression;
    }

    /// <summary>
    /// Parses a persisted column DEFAULT, which is the canonical text of one non-NULL literal, and
    /// returns the literal's value text — what column coercion consumes.
    /// </summary>
    /// <param name="text">The persisted text.</param>
    /// <param name="subject">Names the definition for the error message.</param>
    /// <returns>The literal's value text.</returns>
    /// <exception cref="DatabaseException">The text is not one non-NULL literal.</exception>
    internal static string LoadDefaultValue(string text, string subject)
    {
        if (Load(text, subject) is not SqlLiteralExpression { LiteralType: not SqlLiteralType.Null } literal)
        {
            throw new DatabaseException(
                $"{subject} cannot be loaded: its persisted definition '{text}' is not a literal value. {DamagedCatalogHint}");
        }

        return literal.Value;
    }

    /// <summary>
    /// Binds a loaded expression to the row shape it is evaluated over: every column and
    /// collation must resolve, and the expression must be a row expression — no parameter,
    /// subquery, <c>*</c>, aggregate, or unresolved CAST target, none of which has a value
    /// against one row.
    /// </summary>
    /// <remarks>
    /// Binding checks only what evaluation needs. The rules a statement or DDL applies when it
    /// accepts an expression — operand typing such as a sign over a non-numeric operand, or a
    /// construct a CHECK may not declare — are not applied again: the engine that stored the
    /// definition accepted it, so tightening such a rule never makes an existing database
    /// refuse to open (or an unrelated DDL fail). A row the evaluator cannot evaluate fails its
    /// own statement with the evaluator's coded error, as for any other expression.
    /// </remarks>
    /// <param name="expression">The loaded expression.</param>
    /// <param name="evaluator">The evaluator over the row shape the expression binds to.</param>
    /// <exception cref="DatabaseException">A column or collation does not resolve, or the expression is not a row expression.</exception>
    internal static void Bind(SqlExpression expression, SqlExpressionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(evaluator);

        switch (expression)
        {
            case SqlColumnReferenceExpression column:
                evaluator.ResolveColumn(column);
                break;
            case SqlCollateExpression collate:
                Collation.FromName(collate.CollationName);
                break;
            case SqlCastExpression { TargetTypeInfo: null } cast:
                throw new DatabaseException($"CAST target '{cast.TargetType}' has not been resolved.");
            case SqlParameterExpression or SqlSubqueryExpression or SqlExistsExpression or SqlStarExpression or SqlInExpression { Values: null }:
                throw new DatabaseException("Parameters, subqueries and * have no value in a persisted row expression.");
            case SqlFunctionCallExpression call when call.FunctionName.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX":
                throw new DatabaseException($"Aggregate function '{call.FunctionName}' has no value in a persisted row expression.");
        }

        foreach (var child in SqlPlanner.Children(expression))
        {
            Bind(child, evaluator);
        }
    }

    private static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SqlExpression? expression, out string? problem)
    {
        expression = null;
        var statement = new SqlQueryParser().Parse($"SELECT * FROM {carrierTable} WHERE {text}");
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (error is not null)
        {
            problem = $"{error.Code}: {error.Message}";
            return false;
        }

        if (statement is not SqlQueryStatement { SqlExpression: SqlSelectExpression select } ||
            select.Where is null || select.IsDistinct || select.Columns.Count != 1 ||
            select.Columns[0] is not { Expression: SqlStarExpression, Alias: null } ||
            select.From is not { TableName: carrierTable, SchemaName: null, Alias: null } ||
            select.Joins.Count > 0 || select.GroupBy.Count > 0 || select.Having is not null ||
            select.OrderBy.Count > 0 || select.Limit is not null || select.Offset is not null)
        {
            problem = "the text is not exactly one scalar expression";
            return false;
        }

        problem = null;
        expression = select.Where;
        return true;
    }

    /// <summary>
    /// Whether two trees are the same expression: the same nodes, operators, flags, literal
    /// kinds and values, names, and shape. Source positions are ignored.
    /// </summary>
    /// <param name="left">The first tree.</param>
    /// <param name="right">The second tree.</param>
    /// <returns><see langword="true"/> when the trees are structurally equal.</returns>
    internal static bool AreEquivalent(SqlExpression? left, SqlExpression? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        if (left.GetType() != right.GetType() || !SameNode(left, right))
        {
            return false;
        }

        using var leftChildren = SqlPlanner.Children(left).GetEnumerator();
        using var rightChildren = SqlPlanner.Children(right).GetEnumerator();
        while (true)
        {
            bool hasLeft = leftChildren.MoveNext();
            if (hasLeft != rightChildren.MoveNext())
            {
                return false;
            }

            if (!hasLeft)
            {
                return true;
            }

            if (!AreEquivalent(leftChildren.Current, rightChildren.Current))
            {
                return false;
            }
        }
    }

    // Compares what a node carries besides its children, which AreEquivalent walks through
    // SqlPlanner.Children. The counts and presence flags make that child sequence unambiguous.
    private static bool SameNode(SqlExpression left, SqlExpression right) => (left, right) switch
    {
        (SqlBinaryExpression a, SqlBinaryExpression b) => a.Operator == b.Operator,
        (SqlUnaryExpression a, SqlUnaryExpression b) => a.Operator == b.Operator,
        (SqlLiteralExpression a, SqlLiteralExpression b) => a.LiteralType == b.LiteralType && Same(a.Value, b.Value),
        (SqlColumnReferenceExpression a, SqlColumnReferenceExpression b) =>
            Same(a.ColumnName, b.ColumnName) && Same(a.TableAlias, b.TableAlias) && Same(a.SchemaName, b.SchemaName),
        (SqlParameterExpression a, SqlParameterExpression b) => Same(a.ParameterName, b.ParameterName),
        (SqlStarExpression, SqlStarExpression) => true,
        (SqlIsNullExpression a, SqlIsNullExpression b) => a.IsNegated == b.IsNegated,
        (SqlBetweenExpression a, SqlBetweenExpression b) => a.IsNegated == b.IsNegated,
        (SqlLikeExpression a, SqlLikeExpression b) => a.IsNegated == b.IsNegated,
        (SqlInExpression a, SqlInExpression b) => a.IsNegated == b.IsNegated &&
            (a.Values is null) == (b.Values is null) && (a.Values?.Count ?? 0) == (b.Values?.Count ?? 0) &&
            SameQuery(a.Subquery, b.Subquery),
        (SqlCollateExpression a, SqlCollateExpression b) => Same(a.CollationName, b.CollationName),
        (SqlFunctionCallExpression a, SqlFunctionCallExpression b) =>
            Same(a.FunctionName, b.FunctionName) && a.Arguments.Count == b.Arguments.Count,
        (SqlCaseExpression a, SqlCaseExpression b) => (a.Input is null) == (b.Input is null) &&
            (a.ElseResult is null) == (b.ElseResult is null) && a.WhenClauses.Count == b.WhenClauses.Count,
        (SqlCastExpression a, SqlCastExpression b) => a.TargetTypeInfo is { } x && b.TargetTypeInfo is { } y &&
            x.Type == y.Type && x.MaxLength == y.MaxLength && x.Precision == y.Precision && x.Scale == y.Scale,
        (SqlExistsExpression a, SqlExistsExpression b) => a.IsNegated == b.IsNegated && SameQuery(a.Subquery, b.Subquery),
        (SqlSubqueryExpression a, SqlSubqueryExpression b) => SameQuery(a.Select, b.Select),
        _ => false,
    };

    private static bool SameQuery(SqlSelectExpression? left, SqlSelectExpression? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.IsDistinct == right.IsDistinct &&
            SameList(left.Columns, right.Columns, (a, b) => Same(a.Alias, b.Alias) && AreEquivalent(a.Expression, b.Expression)) &&
            SameTable(left.From, right.From) &&
            SameList(left.Joins, right.Joins, (a, b) => a.JoinType == b.JoinType && SameTable(a.Table, b.Table) && AreEquivalent(a.Condition, b.Condition)) &&
            AreEquivalent(left.Where, right.Where) &&
            SameList(left.GroupBy, right.GroupBy, AreEquivalent) &&
            AreEquivalent(left.Having, right.Having) &&
            SameList(left.OrderBy, right.OrderBy, (a, b) => a.IsDescending == b.IsDescending && AreEquivalent(a.Expression, b.Expression)) &&
            AreEquivalent(left.Limit, right.Limit) &&
            AreEquivalent(left.Offset, right.Offset);
    }

    private static bool SameTable(SqlTableReference? left, SqlTableReference? right)
        => left is null || right is null
            ? left is null && right is null
            : Same(left.TableName, right.TableName) && Same(left.SchemaName, right.SchemaName) && Same(left.Alias, right.Alias);

    private static bool SameList<T>(IReadOnlyList<T> left, IReadOnlyList<T> right, Func<T, T, bool> same)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            if (!same(left[index], right[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
}
