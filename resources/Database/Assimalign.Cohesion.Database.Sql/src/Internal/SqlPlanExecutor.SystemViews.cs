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
    private DatabaseResult ExecuteSystemView(SqlSystemViewPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var evaluator = ExecutionEvaluator(_subqueryValues, cancellationToken);
        var matches = new List<object?[]>();
        statement.Metrics.AccessPath = "system-view";

        var rows = ReadSystemRelation(plan.View, statement, cancellationToken);
        foreach (var values in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            statement.Metrics.RecordsExamined++;
            if (evaluator.Matches(plan.Where, values))
            {
                matches.Add(values);
            }
        }

        var projected = ProjectAndSortRows(matches, plan.Projections, plan.OrderBy, evaluator, plan.OrderByProjections, cancellationToken);

        if (plan.IsDistinct)
        {
            projected = Deduplicate(projected, plan.Projections, evaluator);
        }

        var columns = new DatabaseColumn[plan.Projections.Count];
        for (int i = 0; i < plan.Projections.Count; i++)
        {
            columns[i] = new DatabaseColumn
            {
                Name = plan.Projections[i].Name, Ordinal = i, Type = plan.Projections[i].Type,
                MaxLength = plan.Projections[i].MaxLength,
                IsNullable = plan.Projections[i].IsNullable,
            };
        }
        return new SqlMaterializedResultSet(columns, ApplyRowWindow(projected, plan.Limit, plan.Offset));
    }

    private IEnumerable<object?[]> ReadSystemRelation(
        SqlSystemViewDefinition view, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var snapshot = statement.RequireCatalogSnapshot();
        return view.Schema == "COHESION_SCHEMA" ? view.Name switch
        {
            SqlSystemViews.FunctionsView => EnumerateFunctionRows(_definitions.Functions.Catalog, snapshot),
            "TYPES" => EnumerateTypeRows(false, snapshot),
            "DOMAINS" => EnumerateTypeRows(true, snapshot),
            _ => EnumerateSystemViewRows(view, statement, cancellationToken),
        } : EnumerateSystemViewRows(view, statement, cancellationToken);
    }

    private IEnumerable<object?[]> EnumerateSystemViewRows(
        SqlSystemViewDefinition view, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Tables, columns, constraints, and indexes all come from the same
        // immutable catalog snapshot as the statement, including FK targets.
        var catalog = statement.RequireCatalogSnapshot();
        string database = statement.DatabaseName;
        if (view.Name == "SCHEMATA")
        {
            foreach (var schema in catalog.Namespaces)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // DatabaseObjectOwner describes compiled ownership, not an SQL principal.
                yield return [database, schema.Name, null, null, null, null, null];
            }
            yield break;
        }
        foreach (var stored in catalog.Views)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (view.Name)
            {
                case "TABLES": yield return [database, stored.Schema, stored.Name, "VIEW"]; break;
                case "VIEWS": yield return [database, stored.Schema, stored.Name, stored.Definition, "NONE", "NO"]; break;
                case "OBJECT_OWNERSHIP": yield return [database, stored.Schema, stored.Name, "VIEW", stored.Name, SystemOwner(stored.Owner), stored.OwningSchema]; break;
                case "COLUMNS":
                    var liveColumns = new SqlPlanner(_catalog, null, _definitions.Functions, cancellationToken).ViewColumns(stored, catalog, _definitions.NativeFunctions);
                    for (int i = 0; i < liveColumns.Count; i++)
                    {
                        var column = liveColumns[i];
                        yield return [database, stored.Schema, stored.Name, column.Name, (long)i + 1, null, column.IsNullable ? "YES" : "NO", SystemDataType(column.Type.Type),
                            column.Type.Type is DatabaseType.String or DatabaseType.Character ? (long?)column.Type.MaxLength : null,
                            null, SystemNumericPrecision(column.Type), SystemNumericRadix(column.Type.Type), SystemNumericScale(column.Type),
                            SystemDateTimePrecision(column.Type.Type), "NO", null, null, null, null, null, null];
                    }
                    break;
            }
        }
        if (view.Name == "VIEWS") { yield break; }
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
                            column.Type.Type is DatabaseType.String or DatabaseType.Character ? (long?)column.Type.MaxLength : null,
                            null, // No character-set byte bound is stored in the catalog.
                            SystemNumericPrecision(column.Type), SystemNumericRadix(column.Type.Type), SystemNumericScale(column.Type),
                            SystemDateTimePrecision(column.Type.Type),
                            column.Identity is null ? "NO" : "YES",
                            column.Identity is { } identity ? identity.IsAlways ? "ALWAYS" : "BY DEFAULT" : null,
                            column.Identity?.Start.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            column.Identity?.Increment.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            column.Identity is null ? null : SqlPlanner.IdentityTypeBounds(column.Type.Type, column.Identity.Increment).Maximum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            column.Identity is null ? null : SqlPlanner.IdentityTypeBounds(column.Type.Type, column.Identity.Increment).Minimum.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            column.Identity is null ? null : "NO",
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
                            constraint.OnDelete switch
                            {
                                SqlCatalogReferentialAction.Cascade => "CASCADE",
                                SqlCatalogReferentialAction.Restrict => "RESTRICT",
                                SqlCatalogReferentialAction.NoAction => "NO ACTION",
                                SqlCatalogReferentialAction.SetNull => "SET NULL",
                                SqlCatalogReferentialAction.SetDefault => "SET DEFAULT",
                                _ => throw new DatabaseException(ErrorMessages.DeleteActionUnknown),
                            },
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

    private IEnumerable<object?[]> EnumerateTypeRows(bool domainsOnly, SqlCatalogSnapshot snapshot)
    {
        foreach (var type in _definitions.Functions.Types)
        {
            if (type.IsSqlDefined) { continue; }
            if (domainsOnly)
            {
                if (type.IsDomain) { yield return [type.Name, type.BaseType!.Name, "NO"]; }
            }
            else { yield return [type.Name, type.BaseType?.Name ?? type.Name, type.IsDomain ? "YES" : "NO", "NO"]; }
        }
        foreach (var domain in snapshot.Domains)
        {
            yield return domainsOnly ? [domain.Name, domain.BaseType, "YES"] : [domain.Name, domain.BaseType, "YES", "YES"];
        }
    }

    /// <summary>Frozen native overloads, persisted SQL overloads from the catalog snapshot, and special forms.</summary>
    private static IEnumerable<object?[]> EnumerateFunctionRows(SqlFunctionCatalog catalog, SqlCatalogSnapshot snapshot)
    {
        foreach (var function in catalog)
        {
            if (function is SqlExpressionFunction) { continue; }
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
                // An aggregate is never folded and never admitted in a CHECK, so volatility does not
                // apply to it: NULL, rather than a VOLATILE that would call SUM non-deterministic.
                function.Kind == SqlFunctionKind.Aggregate ? null : function.Volatility switch
                {
                    SqlFunctionVolatility.Immutable => "IMMUTABLE",
                    SqlFunctionVolatility.Stable => "STABLE",
                    _ => "VOLATILE",
                },
                function.NullBehavior == SqlNullBehavior.ReturnsNullOnNullInput ? "RETURNS NULL ON NULL INPUT" : "CALLED ON NULL INPUT",
                SqlStandardLibrary.Contains(function) ? "YES" : "NO",
                "NO",
            ];
        }

        foreach (var function in snapshot.Functions)
        {
            yield return [function.Name, "SCALAR", string.Join(", ", function.ParameterTypes),
                (long)function.ParameterTypes.Count, function.ReturnType, function.Volatility,
                function.IsStrict ? "RETURNS NULL ON NULL INPUT" : "CALLED ON NULL INPUT", "NO", "YES"];
        }

        foreach (string form in SqlStandardLibrary.SpecialForms)
        {
            yield return [form, "SPECIAL FORM", null, null, null, null, null, "YES", "NO"];
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
    private static string? SystemColumnDefault(SqlCatalogColumn column)
    {
        if (column.DefaultLiteral is null) { return null; }
        var expression = SqlPersistedExpression.Load(column.DefaultLiteral, $"DEFAULT of column '{column.Name}'");
        if (expression is not SqlLiteralExpression { LiteralType: SqlLiteralType.String } literal) { return column.DefaultLiteral; }
        if (column.Type.Type is not (DatabaseType.Date or DatabaseType.Time or DatabaseType.DateTime or DatabaseType.DateTimeOffset or DatabaseType.TimeSpan or DatabaseType.Binary)) { return column.DefaultLiteral; }
        object? value = ResolveDefault(column, literal.Value);
        return value switch
        {
            DateOnly date => "DATE '" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'",
            TimeOnly time => "TIME '" + time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "'",
            DateTime timestamp => "TIMESTAMP '" + timestamp.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.') + "'",
            DateTimeOffset instant => "TIMESTAMPTZ '" + instant.ToString("yyyy-MM-dd HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture) + "'",
            TimeSpan interval => "INTERVAL '" + (interval.Ticks < 0 ? "-PT" : "PT") + (Math.Abs((decimal)interval.Ticks) / TimeSpan.TicksPerSecond).ToString("0.#######", CultureInfo.InvariantCulture) + "S'",
            byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
            _ => column.DefaultLiteral,
        };
    }

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
        DatabaseType.Character => "CHARACTER",
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
