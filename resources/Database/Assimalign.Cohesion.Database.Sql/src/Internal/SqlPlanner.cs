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
            SqlCreateIndexExpression createIndex => PlanCreateIndex(createIndex),
            SqlDropIndexExpression dropIndex => PlanDropIndex(dropIndex),
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
            isCountStar,
            SelectAccessPath(table, select.Where));
    }

    // ── Access-path selection (rule-based, by design) ──────────────────

    /// <summary>
    /// Picks the SELECT's access path: an index seek when the WHERE clause has
    /// sargable predicates on an index's leading key columns, the per-object
    /// scan otherwise. Selection is rule-based (the MVP planner's contract —
    /// no cost model): the index with the longest equality prefix wins; ties
    /// prefer a usable range bound, then uniqueness, then name (deterministic).
    /// The full WHERE always remains the residual predicate, so a wrong-looking
    /// choice can cost performance but never correctness.
    /// </summary>
    private SqlAccessPath SelectAccessPath(SqlCatalogTable table, SqlExpression? where)
    {
        if (where is null)
        {
            return SqlScanPath.Instance;
        }

        var indexes = _catalog.GetIndexes(table.ObjectId);

        if (indexes.Count == 0)
        {
            return SqlScanPath.Instance;
        }

        // Top-level AND conjuncts → per-column sargable predicates.
        var predicates = new Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>>();
        CollectSargablePredicates(table, where, predicates);

        if (predicates.Count == 0)
        {
            return SqlScanPath.Instance;
        }

        SqlIndexSeekPath? best = null;
        int bestPrefix = -1;
        bool bestHasRange = false;

        foreach (var index in indexes.OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase))
        {
            var seek = TryBuildSeek(table, index, predicates, out int prefixLength, out bool hasRange);

            if (seek is null)
            {
                continue;
            }

            bool better =
                prefixLength > bestPrefix
                || (prefixLength == bestPrefix && hasRange && !bestHasRange)
                || (prefixLength == bestPrefix && hasRange == bestHasRange && index.IsUnique && best is { Index.IsUnique: false });

            if (better)
            {
                best = seek;
                bestPrefix = prefixLength;
                bestHasRange = hasRange;
            }
        }

        return (SqlAccessPath?)best ?? SqlScanPath.Instance;
    }

    /// <summary>
    /// Builds a seek over one index from the collected predicates: the longest
    /// equality prefix over the leading key columns, plus range bounds on the
    /// following key column when its type's evaluator order provably matches the
    /// codec's byte order. Returns null when the index contributes nothing.
    /// </summary>
    private static SqlIndexSeekPath? TryBuildSeek(
        SqlCatalogTable table,
        SqlCatalogIndex index,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>> predicates,
        out int prefixLength,
        out bool hasRange)
    {
        prefixLength = 0;
        hasRange = false;

        var equalityValues = new List<object?>();

        foreach (string columnName in index.ColumnNames)
        {
            int ordinal = FindColumnOrdinal(table, columnName);

            if (!predicates.TryGetValue(ordinal, out var columnPredicates))
            {
                break;
            }

            object? equality = null;
            bool hasEquality = false;

            foreach (var (op, value) in columnPredicates)
            {
                if (op == SqlBinaryOperator.Equal)
                {
                    equality = value;
                    hasEquality = true;
                    break;
                }
            }

            if (!hasEquality)
            {
                // No equality on this key column: a range bound here ends the seek.
                if (IsRangeSargable(table.Columns[ordinal].Type.Type))
                {
                    SqlSeekBound? lower = null;
                    SqlSeekBound? upper = null;

                    foreach (var (op, value) in columnPredicates)
                    {
                        switch (op)
                        {
                            case SqlBinaryOperator.GreaterThan:
                                lower = Tighter(lower, new SqlSeekBound(value, Inclusive: false), isLower: true);
                                break;
                            case SqlBinaryOperator.GreaterOrEqual:
                                lower = Tighter(lower, new SqlSeekBound(value, Inclusive: true), isLower: true);
                                break;
                            case SqlBinaryOperator.LessThan:
                                upper = Tighter(upper, new SqlSeekBound(value, Inclusive: false), isLower: false);
                                break;
                            case SqlBinaryOperator.LessOrEqual:
                                upper = Tighter(upper, new SqlSeekBound(value, Inclusive: true), isLower: false);
                                break;
                        }
                    }

                    if (lower is not null || upper is not null)
                    {
                        prefixLength = equalityValues.Count;
                        hasRange = true;
                        return new SqlIndexSeekPath(index, equalityValues, lower, upper);
                    }
                }

                break;
            }

            equalityValues.Add(equality);
        }

        if (equalityValues.Count == 0)
        {
            return null;
        }

        prefixLength = equalityValues.Count;
        return new SqlIndexSeekPath(index, equalityValues, Lower: null, Upper: null);
    }

    /// <summary>
    /// Keeps the tighter of two candidate bounds (the residual predicate makes
    /// either choice correct; tighter just reads fewer entries).
    /// </summary>
    private static SqlSeekBound? Tighter(SqlSeekBound? current, SqlSeekBound candidate, bool isLower)
    {
        if (current is null || current.Value.Value is null || candidate.Value is null)
        {
            return candidate;
        }

        int comparison = SqlExpressionEvaluator.Compare(candidate.Value, current.Value.Value);
        bool candidateTighter = isLower ? comparison > 0 : comparison < 0;
        return candidateTighter ? candidate : current;
    }

    /// <summary>
    /// Walks the WHERE clause's top-level AND conjuncts and collects sargable
    /// comparisons: <c>column op comparand</c> (either side) where the comparand
    /// is plan-time evaluable (no column references), non-null, and coercible to
    /// the column's storage type, plus <c>column BETWEEN low AND high</c> as its
    /// two bounds. Everything else is left to the residual predicate.
    /// </summary>
    private void CollectSargablePredicates(
        SqlCatalogTable table,
        SqlExpression expression,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>> predicates)
    {
        if (expression is SqlBinaryExpression { Operator: SqlBinaryOperator.And } conjunction)
        {
            CollectSargablePredicates(table, conjunction.Left, predicates);
            CollectSargablePredicates(table, conjunction.Right, predicates);
            return;
        }

        if (expression is SqlBetweenExpression { IsNegated: false } between &&
            between.Operand is SqlColumnReferenceExpression betweenColumn)
        {
            TryAddPredicate(table, betweenColumn, SqlBinaryOperator.GreaterOrEqual, between.Low, predicates);
            TryAddPredicate(table, betweenColumn, SqlBinaryOperator.LessOrEqual, between.High, predicates);
            return;
        }

        if (expression is not SqlBinaryExpression binary)
        {
            return;
        }

        switch (binary.Operator)
        {
            case SqlBinaryOperator.Equal:
            case SqlBinaryOperator.LessThan:
            case SqlBinaryOperator.LessOrEqual:
            case SqlBinaryOperator.GreaterThan:
            case SqlBinaryOperator.GreaterOrEqual:
                break;
            default:
                return;
        }

        if (binary.Left is SqlColumnReferenceExpression leftColumn)
        {
            TryAddPredicate(table, leftColumn, binary.Operator, binary.Right, predicates);
        }
        else if (binary.Right is SqlColumnReferenceExpression rightColumn)
        {
            TryAddPredicate(table, rightColumn, Flip(binary.Operator), binary.Left, predicates);
        }
    }

    private static SqlBinaryOperator Flip(SqlBinaryOperator op) => op switch
    {
        SqlBinaryOperator.LessThan => SqlBinaryOperator.GreaterThan,
        SqlBinaryOperator.LessOrEqual => SqlBinaryOperator.GreaterOrEqual,
        SqlBinaryOperator.GreaterThan => SqlBinaryOperator.LessThan,
        SqlBinaryOperator.GreaterOrEqual => SqlBinaryOperator.LessOrEqual,
        _ => op,
    };

    private void TryAddPredicate(
        SqlCatalogTable table,
        SqlColumnReferenceExpression column,
        SqlBinaryOperator op,
        SqlExpression comparand,
        Dictionary<int, List<(SqlBinaryOperator Op, object? Value)>> predicates)
    {
        if (ReferencesAnyColumn(comparand))
        {
            return;
        }

        int ordinal;
        try
        {
            ordinal = new SqlExpressionEvaluator(table.Columns, _parameters).ResolveColumn(column);
        }
        catch (DatabaseException)
        {
            return; // resolution errors belong to validation, not access-path selection
        }

        object? value;
        try
        {
            value = new SqlExpressionEvaluator(Array.Empty<SqlCatalogColumn>(), _parameters)
                .Evaluate(comparand, Array.Empty<object?>());
            value = SqlPlanExecutor.CoerceForColumn(value, table.Columns[ordinal]);
        }
        catch (DatabaseException)
        {
            return; // not plan-time evaluable, or not coercible: leave it to the residual
        }

        if (value is null)
        {
            // A null comparand can never satisfy a comparison (SQL three-valued
            // logic); the scan's residual evaluation yields the same empty
            // result without special-casing keys.
            return;
        }

        if (op != SqlBinaryOperator.Equal && !IsRangeSargable(table.Columns[ordinal].Type.Type))
        {
            return;
        }

        if (!predicates.TryGetValue(ordinal, out var list))
        {
            list = new List<(SqlBinaryOperator, object?)>();
            predicates[ordinal] = list;
        }

        list.Add((op, value));
    }

    /// <summary>
    /// The range-sargability type matrix: range seeks are restricted to types
    /// whose evaluator comparison order provably equals the key codec's byte
    /// order. Strings are equality-only — <c>Collation.Binary</c> orders by
    /// code point, which diverges from ordinal UTF-16 comparison for astral
    /// planes (the #854 lesson) — and so are Guid/binary/json.
    /// </summary>
    private static bool IsRangeSargable(DatabaseType type) => type switch
    {
        DatabaseType.Boolean
            or DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64
            or DatabaseType.Float32 or DatabaseType.Float64 or DatabaseType.Decimal
            or DatabaseType.Date or DatabaseType.Time or DatabaseType.DateTime
            or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan => true,
        _ => false,
    };

    private static bool ReferencesAnyColumn(SqlExpression expression)
    {
        if (expression is SqlColumnReferenceExpression)
        {
            return true;
        }

        return Children(expression).Any(ReferencesAnyColumn);
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

    private SqlCreateIndexPlan PlanCreateIndex(SqlCreateIndexExpression create)
    {
        if (string.IsNullOrWhiteSpace(create.IndexName) || create.IndexName == "?")
        {
            throw new DatabaseException("CREATE INDEX requires an index name.");
        }

        if (create.Columns.Count == 0)
        {
            throw new DatabaseException($"CREATE INDEX '{create.IndexName}' requires at least one key column.");
        }

        var table = ResolveTable(create.Table);

        foreach (string column in create.Columns)
        {
            FindColumnOrdinal(table, column); // throws for unknown columns at plan time
        }

        return new SqlCreateIndexPlan(table, create.IndexName, create.Columns, create.IsUnique, create.IfNotExists);
    }

    private SqlDropIndexPlan PlanDropIndex(SqlDropIndexExpression drop)
    {
        if (string.IsNullOrWhiteSpace(drop.IndexName) || drop.IndexName == "?")
        {
            throw new DatabaseException("DROP INDEX requires an index name.");
        }

        if (drop.Table.TableName == "?")
        {
            throw new DatabaseException("DROP INDEX requires the table-qualified form: DROP INDEX <name> ON <table>.");
        }

        return new SqlDropIndexPlan(ResolveTable(drop.Table), drop.IndexName, drop.IfExists);
    }

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
