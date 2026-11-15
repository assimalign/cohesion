using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

// Deviates from the repo RootNamespace rule per shared-source design: stateless migration
// DEFAULT admission keeps its owning schema namespace in the engine and SDK task assemblies.
namespace Assimalign.Cohesion.Database.Sql.Schema.Internal;

internal static class SqlMigrationDefaultLiteral
{
    private const string carrierTable = "__persisted";
    private const string parserOutOfStackCode = "CS00007";

    internal static string? CanonicalDefault(string? text)
    {
        if (text is null) { return null; }
        if (!TryParse(text, out var expression, out string? problem, out bool outOfStack) ||
            !TryParse($"({text})", out _, out problem, out outOfStack, useCeiling: true))
        {
            if (outOfStack) { throw SqlMigrationDefaultBindings.InsufficientStack(); }
            throw SqlMigrationDefaultBindings.InvalidDefinition(text, problem);
        }
        foreach (var node in Nodes(expression))
        {
            SqlMigrationDefaultBindings.ObserveNode(node);
            if (node is SqlCastExpression cast && cast.TargetType.Equals("REGCLASS", StringComparison.OrdinalIgnoreCase) ||
                node is SqlNextValueExpression || node is SqlFunctionCallExpression call &&
                call.FunctionName.ToUpperInvariant() is "NEXTVAL" or "CURRVAL" or "SETVAL" or "LASTVAL")
            {
                throw SqlMigrationDefaultBindings.SequenceUnsupported(node);
            }
        }
        if (expression is not SqlLiteralExpression && expression is not SqlUnaryExpression
            {
                Operator: SqlUnaryOperator.Negate,
                Operand: SqlLiteralExpression { LiteralType: SqlLiteralType.Integer or SqlLiteralType.Float }
            })
        {
            throw SqlMigrationDefaultBindings.LiteralRequired();
        }
        return expression is SqlLiteralExpression { LiteralType: SqlLiteralType.Null }
            ? null : SqlMigrationDefaultBindings.CanonicalizeLiteral(expression);
    }

    private static bool TryParse(string text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SqlExpression? expression,
        out string? problem, out bool outOfStack, bool useCeiling = false)
    {
        expression = null;
        // The declaration uses the normal limit; its additional grouping guard uses the
        // persisted-expression ceiling so the guard adds no declaration nesting restriction.
        // Options stay local so linked consumers retain no mutable parser state.
        var statement = new SqlQueryParser(new SqlQueryParserOptions
        {
            ExpressionNestingLimit = useCeiling
                ? SqlQueryParserOptions.MaximumExpressionNestingLimit
                : SqlQueryParserOptions.DefaultExpressionNestingLimit,
        }).Parse($"SELECT * FROM {carrierTable} WHERE {text}");
        var error = statement.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        outOfStack = error?.Code == parserOutOfStackCode;
        if (error is not null)
        {
            problem = $"{error.Code}: {error.Message}";
            return false;
        }

        if (statement is not SqlQueryStatement { SqlExpression: SqlSelectExpression select } ||
            select.Where is null || select.IsDistinct || select.Columns.Count != 1 ||
            select.Columns[0] is not { Expression: SqlStarExpression, Alias: null } ||
            select.From is not [{ Table: SqlTableReference { TableName: carrierTable, SchemaName: null, Alias: null }, Joins.Count: 0 }] ||
            select.GroupBy.Count > 0 || select.Having is not null ||
            select.OrderBy.Count > 0 || select.Limit is not null || select.Offset is not null)
        {
            problem = SqlMigrationDefaultBindings.ScalarExpressionRequired;
            return false;
        }

        problem = null;
        expression = select.Where;
        return true;
    }

    // One deterministic traversal for persisted calls, including CTEs, set operands,
    // derived bodies and scalar/set/EXISTS subqueries.
    private static IEnumerable<SqlExpression> Nodes(SqlQueryBodyExpression query)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var cte in query.CommonTables)
        { foreach (var node in Nodes(cte.Body)) { yield return node; } }
        if (query is SqlSetOperationExpression set)
        {
            foreach (var node in Nodes(set.Left)) { yield return node; }
            foreach (var node in Nodes(set.Right)) { yield return node; }
        }
        if (query is SqlSelectExpression select)
        {
            foreach (var relation in Relations(select.From))
            {
                if (relation is SqlDerivedTableReference derived)
                { foreach (var node in Nodes(derived.Query)) { yield return node; } }
            }
        }
        foreach (var root in Expressions(query))
        { foreach (var node in Nodes(root)) { yield return node; } }
    }

    private static IEnumerable<SqlRelationReference> Relations(IReadOnlyList<SqlFromItem> sources)
    {
        foreach (var item in sources)
        {
            foreach (var relation in RelationLeaves(item.Table)) { yield return relation; }
            foreach (var join in item.Joins)
            { foreach (var relation in RelationLeaves(join.Table)) { yield return relation; } }
        }
    }

    private static IEnumerable<SqlRelationReference> RelationLeaves(SqlRelationReference reference)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (reference is not SqlJoinedTableReference group) { yield return reference; }
        else { foreach (var relation in Relations([group.Item])) { yield return relation; } }
    }

    private static IEnumerable<SqlJoinClause> Joins(IReadOnlyList<SqlFromItem> sources)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        foreach (var item in sources)
        {
            if (item.Table is SqlJoinedTableReference first)
            { foreach (var join in Joins([first.Item])) { yield return join; } }
            foreach (var join in item.Joins)
            {
                if (join.Table is SqlJoinedTableReference right)
                { foreach (var child in Joins([right.Item])) { yield return child; } }
                yield return join;
            }
        }
    }

    private static IEnumerable<SqlExpression> Expressions(SqlQueryBodyExpression query)
        => query is SqlSelectExpression { Windows.Count: > 0 } select
            ? OrdinaryExpressions(query).Concat(WindowDefinitions(select)) : OrdinaryExpressions(query);

    private static IEnumerable<SqlExpression> WindowDefinitions(SqlSelectExpression select)
    {
        foreach (var window in select.Windows)
        {
            foreach (var key in window.Specification.PartitionBy) { yield return key; }
            foreach (var key in window.Specification.OrderBy) { yield return key.Expression; }
        }
    }

    private static IEnumerable<SqlExpression> OrdinaryExpressions(SqlQueryBodyExpression query)
    {
        if (query is SqlSelectExpression select)
        {
            foreach (var column in select.Columns) { yield return column.Expression; }
            foreach (var join in Joins(select.From)) { if (join.Condition is { } condition) { yield return condition; } }
            if (select.Where is { } where) { yield return where; }
            foreach (var key in select.GroupBy) { yield return key; }
            if (select.Having is { } having) { yield return having; }
        }
        foreach (var order in query.OrderBy) { yield return order.Expression; }
        if (query.Limit is { } limit) { yield return limit; }
        if (query.Offset is { } offset) { yield return offset; }
    }

    private static IEnumerable<SqlExpression> Nodes(SqlExpression expression, bool queries = true)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        yield return expression;
        var query = expression switch
        {
            SqlSubqueryExpression scalar => scalar.Select,
            SqlExistsExpression exists => exists.Subquery,
            SqlInExpression member => member.Subquery,
            SqlQuantifiedComparisonExpression quantified => quantified.Subquery,
            _ => null,
        };
        if (queries && query is not null)
        { foreach (var node in Nodes(query)) { yield return node; } }
        foreach (var child in Children(expression))
        { foreach (var node in Nodes(child, queries)) { yield return node; } }
    }
    private static IEnumerable<SqlExpression> Children(SqlExpression expression) => expression switch
    {
        SqlJsonFunctionExpression json => json.GetOperands(),
        SqlWindowExpression window => WindowChildren(window),
        SqlGroupingSetExpression grouping => grouping.Items,
        SqlLogicalExpression logical => logical.Operands,
        SqlFunctionCallExpression { Filter: not null } function => FilterOperands(function),
        SqlFunctionCallExpression function => function.OrderBy.Count == 0 ? function.Arguments :
            function.Arguments.Concat(function.OrderBy.Select(key => key.Expression)),
        SqlCollateExpression or SqlBinaryExpression or SqlUnaryExpression or SqlDistinctExpression or SqlBooleanTestExpression or SqlIsJsonExpression or SqlIsNullExpression or SqlBetweenExpression
            or SqlInExpression or SqlQuantifiedComparisonExpression or SqlLikeExpression or SqlCaseExpression or SqlCastExpression => Operands(expression),
        _ => [],
    };

    private static IEnumerable<SqlExpression> WindowChildren(SqlWindowExpression window)
    {
        foreach (var child in Children(window.Function)) { yield return child; }
        if (window.Specification is not { } specification) { yield break; }
        foreach (var key in specification.PartitionBy) { yield return key; }
        foreach (var key in specification.OrderBy) { yield return key.Expression; }
        if (specification.Frame?.Start.Offset is { } start) { yield return start; }
        if (specification.Frame?.End.Offset is { } end) { yield return end; }
    }

    private static IEnumerable<SqlExpression> FilterOperands(SqlFunctionCallExpression call)
    {
        foreach (var argument in call.Arguments) { yield return argument; }
        foreach (var key in call.OrderBy) { yield return key.Expression; }
        yield return call.Filter!;
    }

    /// <summary>The operands of a node that has some, in source order (<see cref="Children"/>).</summary>
    private static IEnumerable<SqlExpression> Operands(SqlExpression expression)
    {
        switch (expression)
        {
            case SqlCollateExpression collate:
                yield return collate.Operand;
                break;
            case SqlBinaryExpression binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case SqlUnaryExpression unary:
                yield return unary.Operand;
                break;
            case SqlDistinctExpression distinct:
                yield return distinct.Left;
                yield return distinct.Right;
                break;
            case SqlBooleanTestExpression test:
                yield return test.Operand;
                break;
            case SqlIsJsonExpression json:
                yield return json.Operand;
                break;
            case SqlIsNullExpression isNull:
                yield return isNull.Operand;
                break;
            case SqlBetweenExpression between:
                yield return between.Operand;
                yield return between.Low;
                yield return between.High;
                break;
            case SqlQuantifiedComparisonExpression quantified:
                yield return quantified.Operand;
                break;
            case SqlInExpression inExpression:
                yield return inExpression.Operand;
                if (inExpression.Values is not null)
                {
                    foreach (var value in inExpression.Values)
                    {
                        yield return value;
                    }
                }
                break;
            case SqlLikeExpression like:
                yield return like.Operand;
                yield return like.Pattern;
                if (like.Escape is not null) { yield return like.Escape; }
                break;
            case SqlCaseExpression caseExpression:
                if (caseExpression.Input is not null)
                {
                    yield return caseExpression.Input;
                }
                foreach (var when in caseExpression.WhenClauses)
                {
                    yield return when.Condition;
                    yield return when.Result;
                }
                if (caseExpression.ElseResult is not null)
                {
                    yield return caseExpression.ElseResult;
                }
                break;
            case SqlCastExpression cast:
                yield return cast.Operand;
                break;
        }
    }

}
