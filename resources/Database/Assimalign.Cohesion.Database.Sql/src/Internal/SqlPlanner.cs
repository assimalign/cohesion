using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// The rule-based planner: binds a parsed statement against the catalog, validates
/// it against the executor's supported surface, and produces a <see cref="SqlPlan"/>.
/// Unsupported dialect features fail here with precise messages rather than
/// misexecuting.
/// </summary>
internal sealed class SqlPlanner
{
    /// <summary>
    /// The schema used when a table reference has none.
    /// </summary>
    internal const string DefaultSchema = "dbo";

    private readonly ISqlCatalog _catalog;
    private readonly IReadOnlyDictionary<string, object?>? _parameters;

    internal SqlPlanner(ISqlCatalog catalog, IReadOnlyDictionary<string, object?>? parameters)
    {
        _catalog = catalog;
        _parameters = parameters;
    }

    internal SqlPlan Plan(SqlQueryExpression expression)
    {
        return expression switch
        {
            SqlSelectExpression select => PlanSelect(select),
            SqlInsertExpression insert => PlanInsert(insert),
            SqlUpdateExpression update => PlanUpdate(update),
            SqlDeleteExpression delete => PlanDelete(delete),
            SqlCreateTableExpression create => PlanCreateTable(create),
            SqlDropTableExpression drop => PlanDropTable(drop),
            SqlAlterTableExpression alter => PlanAlterTable(alter),
            _ => throw new DatabaseException($"Statement type {expression.CommandType} is not supported by the executor yet."),
        };
    }

    private SqlSelectPlan PlanSelect(SqlSelectExpression select)
    {
        if (select.Joins.Count > 0)
        {
            throw new DatabaseException("JOIN is not supported by the executor yet.");
        }

        if (select.GroupBy.Count > 0 || select.Having is not null)
        {
            throw new DatabaseException("GROUP BY / HAVING are not supported by the executor yet.");
        }

        if (select.From is null)
        {
            throw new DatabaseException("SELECT requires a FROM table.");
        }

        var table = ResolveTable(select.From);
        var evaluator = new SqlExpressionEvaluator(table.Columns, _parameters);

        // Lone COUNT(*) is the one aggregate the executor supports.
        bool isCountStar =
            select.Columns.Count == 1 &&
            select.Columns[0].Expression is SqlFunctionCallExpression { Arguments.Count: 1 } call &&
            string.Equals(call.FunctionName, "COUNT", StringComparison.OrdinalIgnoreCase) &&
            call.Arguments[0] is SqlStarExpression;

        var projections = new List<SqlProjection>();

        if (isCountStar)
        {
            projections.Add(new SqlProjection(select.Columns[0].Alias ?? "count", null, null, DatabaseType.Int64));
        }
        else
        {
            foreach (var column in select.Columns)
            {
                if (ContainsAggregate(column.Expression))
                {
                    throw new DatabaseException("Aggregate functions (other than a lone COUNT(*)) are not supported by the executor yet.");
                }

                if (column.Expression is SqlStarExpression)
                {
                    for (int i = 0; i < table.Columns.Count; i++)
                    {
                        projections.Add(new SqlProjection(table.Columns[i].Name, i, null, table.Columns[i].Type.Type));
                    }
                }
                else if (column.Expression is SqlColumnReferenceExpression reference)
                {
                    int ordinal = evaluator.ResolveColumn(reference);
                    projections.Add(new SqlProjection(
                        column.Alias ?? table.Columns[ordinal].Name, ordinal, null, table.Columns[ordinal].Type.Type));
                }
                else
                {
                    ValidateExpression(column.Expression, evaluator);
                    projections.Add(new SqlProjection(
                        column.Alias ?? $"column{projections.Count + 1}", null, column.Expression, DatabaseType.Null));
                }
            }
        }

        if (select.Where is not null)
        {
            ValidateExpression(select.Where, evaluator);
        }

        foreach (var orderBy in select.OrderBy)
        {
            ValidateExpression(orderBy.Expression, evaluator);
        }

        return new SqlSelectPlan(
            table,
            projections,
            select.Where,
            select.OrderBy,
            EvaluateCount(select.Limit, "LIMIT"),
            EvaluateCount(select.Offset, "OFFSET"),
            select.IsDistinct,
            isCountStar);
    }

    private SqlInsertPlan PlanInsert(SqlInsertExpression insert)
    {
        if (insert.SelectSource is not null)
        {
            throw new DatabaseException("INSERT ... SELECT is not supported by the executor yet.");
        }

        if (insert.Values is null || insert.Values.Count == 0)
        {
            throw new DatabaseException("INSERT requires a VALUES list.");
        }

        var table = ResolveTable(insert.Table);

        IReadOnlyList<int> targetOrdinals;

        if (insert.Columns is null)
        {
            targetOrdinals = Enumerable.Range(0, table.Columns.Count).ToList();
        }
        else
        {
            var ordinals = new List<int>(insert.Columns.Count);
            foreach (string name in insert.Columns)
            {
                int ordinal = FindColumnOrdinal(table, name);
                ordinals.Add(ordinal);
            }
            targetOrdinals = ordinals;
        }

        foreach (var row in insert.Values)
        {
            if (row.Count != targetOrdinals.Count)
            {
                throw new DatabaseException(
                    $"INSERT row has {row.Count} values but {targetOrdinals.Count} target columns.");
            }
        }

        return new SqlInsertPlan(table, targetOrdinals, insert.Values);
    }

    private SqlUpdatePlan PlanUpdate(SqlUpdateExpression update)
    {
        var table = ResolveTable(update.Table);
        var evaluator = new SqlExpressionEvaluator(table.Columns, _parameters);

        var assignments = new List<(int Ordinal, SqlExpression Value)>(update.Assignments.Count);
        foreach (var assignment in update.Assignments)
        {
            int ordinal = FindColumnOrdinal(table, assignment.ColumnName);
            ValidateExpression(assignment.Value, evaluator);
            assignments.Add((ordinal, assignment.Value));
        }

        if (update.Where is not null)
        {
            ValidateExpression(update.Where, evaluator);
        }

        return new SqlUpdatePlan(table, assignments, update.Where);
    }

    private SqlDeletePlan PlanDelete(SqlDeleteExpression delete)
    {
        var table = ResolveTable(delete.Table);

        if (delete.Where is not null)
        {
            ValidateExpression(delete.Where, new SqlExpressionEvaluator(table.Columns, _parameters));
        }

        return new SqlDeletePlan(table, delete.Where);
    }

    private SqlCreateTablePlan PlanCreateTable(SqlCreateTableExpression create)
    {
        string schema = create.Table.SchemaName ?? DefaultSchema;
        var columns = new List<SqlCatalogColumn>(create.Columns.Count);
        var primaryKey = new List<string>();

        foreach (var definition in create.Columns)
        {
            var typeInfo = ResolveTypeName(definition.DataType, definition.ColumnName);

            string? defaultLiteral = definition.DefaultValue switch
            {
                null => null,
                SqlLiteralExpression literal => literal.LiteralType == SqlLiteralType.Null ? null : literal.Value,
                _ => throw new DatabaseException(
                    $"Column '{definition.ColumnName}': only literal DEFAULT values are supported."),
            };

            // PRIMARY KEY columns are implicitly NOT NULL.
            bool nullable = definition.IsNullable && !definition.IsPrimaryKey;
            columns.Add(new SqlCatalogColumn(definition.ColumnName, typeInfo, nullable, defaultLiteral));

            if (definition.IsPrimaryKey)
            {
                primaryKey.Add(definition.ColumnName);
            }
        }

        return new SqlCreateTablePlan(schema, create.Table.TableName, columns, primaryKey, create.IfNotExists);
    }

    private SqlDropTablePlan PlanDropTable(SqlDropTableExpression drop)
        => new(drop.Table.SchemaName ?? DefaultSchema, drop.Table.TableName, drop.IfExists);

    private SqlPlan PlanAlterTable(SqlAlterTableExpression alter)
    {
        string schema = alter.Table.SchemaName ?? DefaultSchema;

        return alter.Action switch
        {
            SqlAlterAddColumnAction add => new SqlAddColumnPlan(
                schema,
                alter.Table.TableName,
                new SqlCatalogColumn(
                    add.Column.ColumnName,
                    ResolveTypeName(add.Column.DataType, add.Column.ColumnName),
                    add.Column.IsNullable && !add.Column.IsPrimaryKey)),
            SqlAlterDropColumnAction drop => new SqlDropColumnPlan(schema, alter.Table.TableName, drop.ColumnName),
            _ => throw new DatabaseException("This ALTER TABLE action is not supported by the executor yet."),
        };
    }

    private SqlCatalogTable ResolveTable(SqlTableReference reference)
    {
        string schema = reference.SchemaName ?? DefaultSchema;

        if (!_catalog.TryGetTable(schema, reference.TableName, out var table))
        {
            throw new DatabaseException($"Table '{schema}.{reference.TableName}' does not exist.");
        }

        return table;
    }

    private static int FindColumnOrdinal(SqlCatalogTable table, string columnName)
    {
        for (int i = 0; i < table.Columns.Count; i++)
        {
            if (string.Equals(table.Columns[i].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new DatabaseException($"Table '{table.Schema}.{table.Name}' has no column named '{columnName}'.");
    }

    /// <summary>
    /// Parses a DDL type token like <c>VARCHAR(100)</c> or <c>DECIMAL(18, 4)</c>
    /// through the declared dialect's type table.
    /// </summary>
    private static DatabaseTypeInfo ResolveTypeName(string dataType, string columnName)
    {
        string name = dataType;
        int? first = null;
        int? second = null;

        int open = dataType.IndexOf('(');
        if (open >= 0)
        {
            name = dataType[..open];
            string arguments = dataType[(open + 1)..].TrimEnd(')');
            var parts = arguments.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 0)
            {
                first = int.Parse(parts[0], CultureInfo.InvariantCulture);
            }

            if (parts.Length > 1)
            {
                second = int.Parse(parts[1], CultureInfo.InvariantCulture);
            }
        }

        // A second argument is always a scale (DECIMAL(p, s)); SqlTypeNames moves a
        // single argument from length to precision for decimal kinds.
        if (!SqlTypeNames.TryResolve(name, first, second is null ? null : first, second, out var typeInfo))
        {
            throw new DatabaseException($"Column '{columnName}': unknown type '{dataType}'.");
        }

        return typeInfo;
    }

    private long? EvaluateCount(SqlExpression? expression, string clause)
    {
        if (expression is null)
        {
            return null;
        }

        object? value = new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), _parameters)
            .Evaluate(expression, Array.Empty<object?>());

        return value switch
        {
            long number when number >= 0 => number,
            int number when number >= 0 => number,
            _ => throw new DatabaseException($"{clause} requires a non-negative integer."),
        };
    }

    private static void ValidateExpression(SqlExpression expression, SqlExpressionEvaluator evaluator)
    {
        switch (expression)
        {
            case SqlSubqueryExpression or SqlExistsExpression:
                throw new DatabaseException("Subqueries are not supported by the executor yet.");
            case SqlColumnReferenceExpression reference:
                evaluator.ResolveColumn(reference); // throws for unknown columns at plan time
                break;
            case SqlInExpression { Values: null }:
                throw new DatabaseException("IN subqueries are not supported by the executor yet.");
        }

        foreach (var child in Children(expression))
        {
            ValidateExpression(child, evaluator);
        }
    }

    private static bool ContainsAggregate(SqlExpression expression)
    {
        if (expression is SqlFunctionCallExpression call &&
            call.FunctionName.ToUpperInvariant() is "COUNT" or "SUM" or "AVG" or "MIN" or "MAX")
        {
            return true;
        }

        return Children(expression).Any(ContainsAggregate);
    }

    private static IEnumerable<SqlExpression> Children(SqlExpression expression)
    {
        switch (expression)
        {
            case SqlBinaryExpression binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case SqlUnaryExpression unary:
                yield return unary.Operand;
                break;
            case SqlIsNullExpression isNull:
                yield return isNull.Operand;
                break;
            case SqlBetweenExpression between:
                yield return between.Operand;
                yield return between.Low;
                yield return between.High;
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
            case SqlFunctionCallExpression function:
                foreach (var argument in function.Arguments)
                {
                    yield return argument;
                }
                break;
            case SqlCastExpression cast:
                yield return cast.Operand;
                break;
        }
    }
}
