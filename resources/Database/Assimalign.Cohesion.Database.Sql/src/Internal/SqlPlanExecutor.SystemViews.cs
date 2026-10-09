using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private QueryResult ExecuteSystemView(SqlSystemViewPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = SqlExpressionEvaluator.ForExecution(_subqueryValues, cancellationToken);
        var matches = new List<object?[]>();
        statement.Metrics.AccessPath = "system-view";

        // The function catalog is the engine's, frozen at its build: no catalog snapshot describes it.
        var rows = plan.View.Name == SqlSystemViews.FunctionsView
            ? EnumerateFunctionRows(_definitions.Functions.Catalog)
            : EnumerateSystemViewRows(plan.View, statement, cancellationToken);
        foreach (var values in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statement.Metrics.RecordsExamined++;
            if (evaluator.Matches(plan.Where, values))
            {
                matches.Add(values);
            }
        }

        var projected = ProjectAndSortRows(matches, plan.Projections, plan.OrderBy, evaluator, plan.OrderByProjections);

        if (plan.IsDistinct)
        {
            projected = Deduplicate(projected, plan.Projections, evaluator);
        }

        IEnumerable<object?[]> window = projected;
        if (plan.Offset is long offset)
        {
            window = window.Skip((int)offset);
        }
        if (plan.Limit is long limit)
        {
            window = window.Take((int)limit);
        }

        var columns = new QueryColumn[plan.Projections.Count];
        for (int i = 0; i < plan.Projections.Count; i++)
        {
            columns[i] = new QueryColumn
            {
                Name = plan.Projections[i].Name, Ordinal = i, Type = plan.Projections[i].Type,
                IsNullable = plan.Projections[i].Expression is SqlCastExpression,
            };
        }
        return new SqlMaterializedResultSet(columns, window.ToList());
    }

    private static IEnumerable<object?[]> EnumerateSystemViewRows(
        SqlSystemViewDefinition view, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Tables, columns, constraints, and indexes all come from the same
        // immutable catalog snapshot as the statement, including FK targets.
        var catalog = statement.CatalogSnapshot;
        if (catalog is null)
        {
            yield break;
        }
        string database = statement.DatabaseName;
        foreach (var table in catalog.Tables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (view.Name)
            {
                case "TABLES":
                    yield return [database, table.Schema, table.Name, "BASE TABLE"];
                    break;

                case "COLUMNS":
                    for (int ordinal = 0; ordinal < table.Columns.Count; ordinal++)
                    {
                        var column = table.Columns[ordinal];
                        yield return
                        [
                            database, table.Schema, table.Name, column.Name, (long)ordinal + 1,
                            SystemColumnDefault(column), column.IsNullable ? "YES" : "NO", SystemDataType(column.Type.Type),
                            column.Type.Type == DatabaseType.String ? (long?)column.Type.MaxLength : null,
                            null, // No character-set byte bound is stored in the catalog.
                            SystemNumericPrecision(column.Type), SystemNumericRadix(column.Type.Type), SystemNumericScale(column.Type),
                            SystemDateTimePrecision(column.Type.Type),
                        ];
                    }
                    break;

                case "TABLE_CONSTRAINTS":
                    foreach (var key in SystemKeyConstraints(catalog, table))
                    {
                        yield return
                        [
                            database, table.Schema, key.Name, database, table.Schema, table.Name,
                            key.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE", "NO", "NO",
                        ];
                    }
                    foreach (var constraint in table.Constraints)
                    {
                        yield return
                        [
                            database, table.Schema, constraint.Name, database, table.Schema, table.Name,
                            constraint.Kind == SqlCatalogConstraintKind.Reference ? "FOREIGN KEY" : "CHECK", "NO", "NO",
                        ];
                    }
                    break;

                case "KEY_COLUMN_USAGE":
                    foreach (var key in SystemKeyConstraints(catalog, table))
                    {
                        for (int ordinal = 0; ordinal < key.Columns.Count; ordinal++)
                        {
                            yield return [database, table.Schema, key.Name, database, table.Schema, table.Name, key.Columns[ordinal], (long)ordinal + 1];
                        }
                    }
                    foreach (var constraint in table.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference))
                    {
                        for (int ordinal = 0; ordinal < constraint.Columns.Count; ordinal++)
                        {
                            yield return [database, table.Schema, constraint.Name, database, table.Schema, table.Name, constraint.Columns[ordinal], (long)ordinal + 1];
                        }
                    }
                    break;

                case "REFERENTIAL_CONSTRAINTS":
                    foreach (var constraint in table.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference))
                    {
                        // Foreign keys store a target column set, not an index
                        // identity. Prefer its primary key, then a stable unique
                        // index name when multiple indexes enforce that same set.
                        string? referencedConstraint = null;
                        SqlCatalogTable? referencedTable = null;
                        if (catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out var target))
                        {
                            referencedTable = target;
                            referencedConstraint = SystemKeyConstraints(catalog, target)
                                .Where(key => SameConstraintColumns(key.Columns, constraint.ReferencedColumns))
                                .OrderByDescending(key => key.IsPrimaryKey)
                                .ThenBy(key => key.Name, StringComparer.OrdinalIgnoreCase)
                                .FirstOrDefault().Name;
                        }
                        yield return
                        [
                            database, table.Schema, constraint.Name,
                            referencedConstraint is null ? null : database,
                            referencedConstraint is null ? null : referencedTable!.Schema,
                            referencedConstraint, "NONE", "RESTRICT",
                            constraint.OnDelete == SqlCatalogReferentialAction.Cascade ? "CASCADE" : "RESTRICT",
                        ];
                    }
                    break;

                case "CHECK_CONSTRAINTS":
                    foreach (var constraint in table.Constraints.Where(constraint => constraint.Kind == SqlCatalogConstraintKind.Check))
                    {
                        yield return [database, table.Schema, constraint.Name, constraint.CheckExpression];
                    }
                    break;

                case "INDEXES":
                    foreach (var index in catalog.GetIndexes(table.ObjectId))
                    {
                        for (int ordinal = 0; ordinal < index.ColumnNames.Count; ordinal++)
                        {
                            yield return
                            [
                                database, table.Schema, table.Name, index.Name, index.ColumnNames[ordinal], (long)ordinal + 1,
                                index.IsUnique ? "YES" : "NO", index.IsPrimaryKey ? "YES" : "NO",
                            ];
                        }
                    }
                    break;

                case "OBJECT_OWNERSHIP":
                    yield return [database, table.Schema, table.Name, "TABLE", table.Name, SystemOwner(table.Owner), table.OwningSchema];
                    foreach (var index in catalog.GetIndexes(table.ObjectId))
                    {
                        yield return [database, table.Schema, table.Name, "INDEX", index.Name, SystemOwner(index.Owner), index.OwningSchema];
                    }
                    break;

                default:
                    throw new DatabaseException($"System view '{view.Schema}.{view.Name}' is not executable.");
            }
        }
    }

    /// <summary>
    /// The rows of <c>COHESION_SCHEMA.FUNCTIONS</c>: every function of the engine's catalog, one row
    /// per overload in registration order (the standard library first), then the special forms,
    /// which are grammar and have no signature (owner decision 66 of 2026-10-09).
    /// </summary>
    private static IEnumerable<object?[]> EnumerateFunctionRows(SqlFunctionCatalog catalog)
    {
        foreach (var function in catalog)
        {
            var types = new StringBuilder();
            var parameters = function.Parameters;
            for (int index = 0; index < parameters.Count; index++)
            {
                types.Append(index == 0 ? string.Empty : ", ").Append(parameters[index].Name);
            }
            if (function.VariadicParameter is { } variadic)
            {
                types.Append(parameters.Count == 0 ? string.Empty : ", ").Append(variadic.Name).Append(" ...");
            }
            else if (parameters.Count == 0 && function.Kind == SqlFunctionKind.Aggregate)
            {
                types.Append('*'); // a parameterless aggregate is called as name(*), as COUNT(*) is
            }

            yield return
            [
                function.Name,
                function.Kind == SqlFunctionKind.Scalar ? "SCALAR" : "AGGREGATE",
                types.ToString(),
                (long)parameters.Count,
                function.ReturnType.Name,
                function.Volatility switch
                {
                    SqlFunctionVolatility.Immutable => "IMMUTABLE",
                    SqlFunctionVolatility.Stable => "STABLE",
                    _ => "VOLATILE",
                },
                function.NullBehavior == SqlNullBehavior.ReturnsNullOnNullInput ? "RETURNS NULL ON NULL INPUT" : "CALLED ON NULL INPUT",
                SqlStandardLibrary.Contains(function) ? "YES" : "NO",
            ];
        }

        foreach (string form in SqlStandardLibrary.SpecialForms)
        {
            yield return [form, "SPECIAL FORM", null, null, null, null, null, "YES"];
        }
    }

    private static IEnumerable<(string Name, IReadOnlyList<string> Columns, bool IsPrimaryKey)> SystemKeyConstraints(
        SqlCatalogSnapshot catalog, SqlCatalogTable table)
    {
        var indexes = catalog.GetIndexes(table.ObjectId);
        // Catalogs created before primary indexes existed still retain the
        // declared primary-key columns. Expose that constraint without inventing
        // an index or pretending any separate unique index is its physical tree.
        if (table.PrimaryKeyColumns.Count > 0 && !indexes.Any(index => index.IsPrimaryKey))
        {
            string baseName = $"PrimaryKey_{table.Name}";
            string name = baseName;
            var names = new HashSet<string>(indexes.Select(index => index.Name), StringComparer.OrdinalIgnoreCase);
            names.UnionWith(table.Constraints.Select(constraint => constraint.Name));
            for (int suffix = 1; names.Contains(name); suffix++)
            {
                name = baseName + "_" + suffix.ToString(CultureInfo.InvariantCulture);
            }
            yield return (name, table.PrimaryKeyColumns, true);
        }
        foreach (var index in indexes.Where(index => index.IsUnique))
        {
            yield return (index.Name, index.ColumnNames, index.IsPrimaryKey);
        }
    }

    private static string SystemOwner(DatabaseObjectOwner owner)
        => owner == DatabaseObjectOwner.Schema ? "Schema" : "Adhoc";

    // The catalog stores a DEFAULT as the canonical SQL text of its literal, which is the
    // default clause ISO's COLUMN_DEFAULT reports.
    private static string? SystemColumnDefault(SqlCatalogColumn column) => column.DefaultLiteral;

    // The catalog retains shared type identities rather than lexical SQL
    // aliases. Report a canonical SQL name; size and precision have ISO columns.
    private static string SystemDataType(DatabaseType type) => type switch
    {
        DatabaseType.Boolean => "BOOLEAN",
        DatabaseType.Int8 => "TINYINT",
        DatabaseType.Int16 => "SMALLINT",
        DatabaseType.Int32 => "INTEGER",
        DatabaseType.Int64 => "BIGINT",
        DatabaseType.Float32 => "REAL",
        DatabaseType.Float64 => "DOUBLE PRECISION",
        DatabaseType.Decimal => "NUMERIC",
        DatabaseType.String => "CHARACTER VARYING",
        DatabaseType.Binary => "BINARY VARYING",
        DatabaseType.Date => "DATE",
        DatabaseType.Time => "TIME",
        DatabaseType.DateTime => "TIMESTAMP",
        DatabaseType.DateTimeOffset => "TIMESTAMP WITH TIME ZONE",
        DatabaseType.TimeSpan => "INTERVAL",
        DatabaseType.Guid => "UUID",
        DatabaseType.Json => "JSON",
        DatabaseType.JsonBinary => "JSONB",
        _ => "UNKNOWN",
    };

    private static long? SystemNumericPrecision(DatabaseTypeInfo type) => type.Type switch
    {
        DatabaseType.Int8 => 8,
        DatabaseType.Int16 => 16,
        DatabaseType.Int32 => 32,
        DatabaseType.Int64 => 64,
        DatabaseType.Float32 => 24,
        DatabaseType.Float64 => 53,
        DatabaseType.Decimal => type.Precision,
        _ => null,
    };

    private static long? SystemNumericRadix(DatabaseType type) => type switch
    {
        DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or
            DatabaseType.Float32 or DatabaseType.Float64 => 2,
        DatabaseType.Decimal => 10,
        _ => null,
    };

    private static long? SystemNumericScale(DatabaseTypeInfo type) => type.Type switch
    {
        DatabaseType.Int8 or DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 => 0,
        DatabaseType.Decimal => type.Scale,
        _ => null,
    };

    private static long? SystemDateTimePrecision(DatabaseType type) => type switch
    {
        DatabaseType.Date => 0,
        DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset => 7,
        _ => null,
    };
}
