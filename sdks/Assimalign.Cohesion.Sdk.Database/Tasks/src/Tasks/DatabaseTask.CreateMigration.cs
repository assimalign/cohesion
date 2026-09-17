using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Types;

using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Database.Tasks;

/// <summary>
/// Creates an ordered SQL migration and matching compiled-schema baseline.
/// </summary>
public sealed partial class CreateDatabaseMigrationTask : DatabaseTask
{
    /// <summary>The desired compiled schema produced by <see cref="CompileDatabaseSchemaTask"/>.</summary>
    [Required]
    public string SchemaModelPath { get; set; } = string.Empty;

    /// <summary>The directory generated migration files are written to.</summary>
    [Required]
    public string MigrationsRoot { get; set; } = string.Empty;

    /// <summary>The source-control-safe migration name.</summary>
    [Required]
    public string MigrationName { get; set; } = string.Empty;

    /// <summary>The selected database model.</summary>
    [Required]
    public string Model { get; set; } = string.Empty;

    /// <summary>The consumer project directory used to resolve relative output paths.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>The generated SQL script path.</summary>
    [Output]
    public string MigrationPath { get; private set; } = string.Empty;

    /// <summary>The generated compiled-schema baseline path.</summary>
    [Output]
    public string BaselinePath { get; private set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        if (string.Equals(Model, "KeyValuePair", StringComparison.Ordinal))
        {
            Log.LogError(
                null,
                "COHDBSDK201",
                null,
                null,
                0,
                0,
                0,
                0,
                "KeyValuePair migrations do not have a relational SQL representation and are not supported by CohesionDatabaseCreateMigration.");
            return false;
        }
        if (!string.Equals(Model, "Sql", StringComparison.Ordinal))
        {
            Log.LogError(
                null,
                "COHDBSDK201",
                null,
                null,
                0,
                0,
                0,
                0,
                $"Database model '{Model}' does not provide migration tooling.");
            return false;
        }

        string migrationName = MigrationName.Trim();
        if (!MigrationNamePattern().IsMatch(migrationName))
        {
            Log.LogError(
                null,
                "COHDBSDK202",
                null,
                null,
                0,
                0,
                0,
                0,
                "CohesionDatabaseMigrationName is required and must contain only letters or digits separated by '.', '_' or '-'.");
            return false;
        }

        try
        {
            string projectDirectory = Path.GetFullPath(ProjectDirectory);
            string schemaPath = ResolvePath(SchemaModelPath, projectDirectory);
            if (!File.Exists(schemaPath))
            {
                Log.LogError(
                    null,
                    "COHDBSDK203",
                    null,
                    schemaPath,
                    0,
                    0,
                    0,
                    0,
                    "The compiled database schema does not exist. Build the project before creating a migration.");
                return false;
            }

            string migrationsRoot = ResolvePath(MigrationsRoot, projectDirectory);
            Directory.CreateDirectory(migrationsRoot);
            BaselineFile? latest = FindLatestBaseline(migrationsRoot);
            CompiledSchema desired = CompiledSchemaSerializer.Read(schemaPath);
            CompiledSchema? current = latest is null
                ? null
                : CompiledSchemaSerializer.Read(latest.Path);
            SchemaMigrationPlan plan = SchemaMigrationPlanner.Plan(current, desired);
            if (plan.IsEmpty)
            {
                Log.LogError(
                    null,
                    "COHDBSDK204",
                    null,
                    schemaPath,
                    0,
                    0,
                    0,
                    0,
                    $"Schema '{desired.Name}' has no changes relative to the newest migration baseline.");
                return false;
            }

            int ordinal = checked((latest?.Ordinal ?? 0) + 1);
            if (ordinal > 9999)
            {
                throw new InvalidOperationException("Database migration ordinals cannot exceed 9999.");
            }

            string prefix = $"{ordinal.ToString("D4", CultureInfo.InvariantCulture)}_{migrationName}";
            MigrationPath = Path.Combine(migrationsRoot, prefix + ".sql");
            BaselinePath = Path.Combine(migrationsRoot, prefix + ".schema.json");
            if (File.Exists(MigrationPath) || File.Exists(BaselinePath))
            {
                Log.LogError(
                    null,
                    "COHDBSDK205",
                    null,
                    migrationsRoot,
                    0,
                    0,
                    0,
                    0,
                    $"Migration ordinal/name '{prefix}' already exists; no files were written.");
                return false;
            }

            string script = SqlMigrationWriter.Write(prefix, plan);
            string baseline = CompiledSchemaSerializer.Serialize(desired);
            WritePair(MigrationPath, script, BaselinePath, baseline);
            Log.LogMessage(
                MessageImportance.High,
                $"Created SQL migration '{MigrationPath}' and baseline '{BaselinePath}'.");
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or DatabaseSchemaValidationException or DatabaseSchemaMigrationException)
        {
            Log.LogError(null, "COHDBSDK206", null, null, 0, 0, 0, 0, exception.Message);
            return false;
        }
    }

    private static BaselineFile? FindLatestBaseline(string migrationsRoot)
    {
        return Directory.EnumerateFiles(migrationsRoot, "*.schema.json", SearchOption.TopDirectoryOnly)
            .Select(path => new { Path = path, Match = BaselinePattern().Match(Path.GetFileName(path)) })
            .Where(static candidate => candidate.Match.Success)
            .Select(static candidate => new BaselineFile(
                int.Parse(candidate.Match.Groups["ordinal"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
                candidate.Path))
            .OrderByDescending(static candidate => candidate.Ordinal)
            .FirstOrDefault();
    }

    private static void WritePair(string firstPath, string firstContent, string secondPath, string secondContent)
    {
        string firstTemporary = firstPath + ".tmp";
        string secondTemporary = secondPath + ".tmp";
        var encoding = new UTF8Encoding(false);
        try
        {
            File.WriteAllText(firstTemporary, firstContent, encoding);
            File.WriteAllText(secondTemporary, secondContent, encoding);
            File.Move(firstTemporary, firstPath);
            try
            {
                File.Move(secondTemporary, secondPath);
            }
            catch
            {
                File.Delete(firstPath);
                throw;
            }
        }
        finally
        {
            File.Delete(firstTemporary);
            File.Delete(secondTemporary);
        }
    }

    private static string ResolvePath(string path, string projectDirectory)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectDirectory, path));

    [GeneratedRegex("^[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MigrationNamePattern();

    [GeneratedRegex("^(?<ordinal>[0-9]{4})_[A-Za-z0-9]+(?:[._-][A-Za-z0-9]+)*\\.schema\\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex BaselinePattern();

    private sealed record BaselineFile(int Ordinal, string Path);

    private static class SqlMigrationWriter
    {
        internal static string Write(string migrationName, SchemaMigrationPlan plan)
        {
            var builder = new StringBuilder();
            builder.Append("-- Cohesion database migration ").Append(migrationName).Append('\n');
            builder.Append("-- Source schema: ").Append(plan.SourceHash ?? "<empty>").Append('\n');
            builder.Append("-- Target schema: ").Append(plan.TargetHash).Append('\n');
            foreach (SchemaMigrationOperation operation in plan.Operations)
            {
                WriteOperation(builder, operation);
            }
            return builder.ToString();
        }

        private static void WriteOperation(StringBuilder builder, SchemaMigrationOperation operation)
        {
            switch (operation.Kind)
            {
                case SchemaMigrationOperationKind.AddTable:
                    WriteAddTable(builder, Required(operation.Table, operation));
                    break;
                case SchemaMigrationOperationKind.DropTable:
                    builder.Append("DROP TABLE IF EXISTS ").Append(QualifiedTable(operation.ObjectName)).Append(";\n");
                    break;
                case SchemaMigrationOperationKind.AddColumn:
                    CompiledSchemaColumn addedColumn = Required(operation.Column, operation);
                    if (!addedColumn.IsNullable)
                    {
                        throw Unsupported(
                            operation,
                            "adding a non-nullable column requires a default or backfill, which the SQL DDL dialect does not model");
                    }
                    builder.Append("ALTER TABLE ").Append(QualifiedTable(Required(operation.ParentName, operation)))
                        .Append(" ADD COLUMN ");
                    WriteColumn(builder, addedColumn, isPrimaryKey: false);
                    builder.Append(";\n");
                    break;
                case SchemaMigrationOperationKind.AlterColumn:
                    throw Unsupported(operation, "ALTER metadata is not implemented by the SQL DDL dialect");
                case SchemaMigrationOperationKind.DropColumn:
                    builder.Append("ALTER TABLE ").Append(QualifiedTable(Required(operation.ParentName, operation)))
                        .Append(" DROP COLUMN ").Append(Identifier(operation.ObjectName)).Append(";\n");
                    break;
                case SchemaMigrationOperationKind.AddIndex:
                    WriteAddIndex(builder, Required(operation.ParentName, operation), Required(operation.Index, operation));
                    break;
                case SchemaMigrationOperationKind.DropIndex:
                    builder.Append("DROP INDEX IF EXISTS ").Append(Identifier(operation.ObjectName))
                        .Append(" ON ").Append(QualifiedTable(Required(operation.ParentName, operation))).Append(";\n");
                    break;
                case SchemaMigrationOperationKind.AlterTable:
                    throw Unsupported(operation, "ALTER metadata is not implemented by the SQL DDL dialect");
                case SchemaMigrationOperationKind.AddCollection:
                case SchemaMigrationOperationKind.AlterCollection:
                case SchemaMigrationOperationKind.DropCollection:
                    throw Unsupported(operation, "key-value collections do not belong to the SQL model");
                default:
                    throw Unsupported(operation, "the operation kind is unknown");
            }
        }

        private static void WriteAddTable(StringBuilder builder, CompiledSchemaTable table)
        {
            if (table.Constraints.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Table '{table.Name}' declares constraints that the SQL DDL executor does not support yet.");
            }

            builder.Append("CREATE TABLE IF NOT EXISTS ").Append(QualifiedTable(table.Name)).Append(" (");
            for (int index = 0; index < table.Columns.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                CompiledSchemaColumn column = table.Columns[index];
                WriteColumn(builder, column, IsPrimaryKeyColumn(table.PrimaryKey, column.Name));
            }

            builder.Append(");\n");
        }

        private static void WriteAddIndex(StringBuilder builder, string table, CompiledSchemaIndex index)
        {
            builder.Append("CREATE ");
            if (index.IsUnique)
            {
                builder.Append("UNIQUE ");
            }
            builder.Append("INDEX IF NOT EXISTS ").Append(Identifier(index.Name))
                .Append(" ON ").Append(QualifiedTable(table))
                .Append(" (").Append(ColumnList(index.Columns)).Append(");\n");
        }

        private static void WriteColumn(StringBuilder builder, CompiledSchemaColumn column, bool isPrimaryKey)
        {
            builder.Append(Identifier(column.Name)).Append(' ').Append(SqlType(column));
            if (isPrimaryKey)
            {
                builder.Append(" PRIMARY KEY");
            }

            builder.Append(isPrimaryKey || !column.IsNullable ? " NOT NULL" : " NULL");
        }

        private static string SqlType(CompiledSchemaColumn column)
        {
            if (!string.IsNullOrWhiteSpace(column.CustomType))
            {
                throw new InvalidOperationException(
                    $"Column '{column.Name}' uses custom type '{column.CustomType}', which the SQL DDL executor does not support yet.");
            }

            return column.Type switch
            {
                DatabaseType.Boolean => "BOOLEAN",
                DatabaseType.Int8 => "TINYINT",
                DatabaseType.Int16 => "SMALLINT",
                DatabaseType.Int32 => "INT",
                DatabaseType.Int64 => "BIGINT",
                DatabaseType.Float32 => "REAL",
                DatabaseType.Float64 => "DOUBLE",
                DatabaseType.Decimal => DecimalTypeName(column),
                DatabaseType.String => SizedTypeName("VARCHAR", "TEXT", column.MaxLength),
                DatabaseType.Binary => SizedTypeName("VARBINARY", "BLOB", column.MaxLength),
                DatabaseType.Date => "DATE",
                DatabaseType.Time => "TIME",
                DatabaseType.DateTime => "TIMESTAMP",
                DatabaseType.DateTimeOffset => "TIMESTAMPTZ",
                DatabaseType.TimeSpan => "INTERVAL",
                DatabaseType.Guid => "UUID",
                DatabaseType.Json => "JSON",
                DatabaseType.JsonBinary => "JSONB",
                _ => throw new InvalidOperationException($"Database type '{column.Type}' has no SQL migration representation.")
            };
        }

        private static string DecimalTypeName(CompiledSchemaColumn column)
        {
            if (column.Scale is not null && column.Precision is null)
            {
                throw new InvalidOperationException(
                    $"Column '{column.Name}' declares a decimal scale without a precision.");
            }

            if (column.Precision is null)
            {
                return "DECIMAL";
            }

            string precision = column.Precision.Value.ToString(CultureInfo.InvariantCulture);
            return column.Scale is null
                ? $"DECIMAL({precision})"
                : $"DECIMAL({precision},{column.Scale.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        private static string SizedTypeName(string sizedName, string unboundedName, int? maxLength)
            => maxLength is null
                ? unboundedName
                : $"{sizedName}({maxLength.Value.ToString(CultureInfo.InvariantCulture)})";

        private static bool IsPrimaryKeyColumn(CompiledSchemaKey? key, string columnName)
            => key?.Columns.Any(keyColumn => string.Equals(keyColumn, columnName, StringComparison.OrdinalIgnoreCase)) == true;

        private static string ColumnList(System.Collections.Generic.IReadOnlyList<string> columns)
            => string.Join(", ", columns.Select(Identifier));

        private static string QualifiedTable(string tableName) => $"dbo.{Identifier(tableName)}";

        private static string Identifier(string value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
            if (!(char.IsLetter(value[0]) || value[0] == '_'))
            {
                throw new InvalidOperationException($"Schema identifier '{value}' cannot be represented by the SQL dialect.");
            }

            for (int index = 1; index < value.Length; index++)
            {
                if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_'))
                {
                    throw new InvalidOperationException($"Schema identifier '{value}' cannot be represented by the SQL dialect.");
                }
            }

            return value;
        }

        private static InvalidOperationException Unsupported(SchemaMigrationOperation operation, string reason)
            => new($"SQL migration operation '{operation.Kind}' for '{QualifiedName(operation)}' is unsupported: {reason}.");

        private static string QualifiedName(SchemaMigrationOperation operation)
            => operation.ParentName is null
                ? operation.ObjectName
                : $"{operation.ParentName}.{operation.ObjectName}";

        private static T Required<T>(T? value, SchemaMigrationOperation operation) where T : class
            => value ?? throw new InvalidOperationException($"Migration operation '{operation.Kind}' for '{operation.ObjectName}' omitted required data.");

        private static string Required(string? value, SchemaMigrationOperation operation)
            => value ?? throw new InvalidOperationException($"Migration operation '{operation.Kind}' for '{operation.ObjectName}' omitted its parent name.");

    }
}
