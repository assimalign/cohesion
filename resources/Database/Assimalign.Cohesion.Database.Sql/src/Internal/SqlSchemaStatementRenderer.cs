using System;
using System.Globalization;
using System.Text;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Renders the SQL engine's supported compiled-schema operations into stable text.
/// </summary>
internal static class SqlSchemaStatementRenderer
{
    private const string defaultSchema = "dbo";

    internal static string CreateTable(CompiledSchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.Constraints.Count > 0)
        {
            throw new DatabaseException(
                $"Table '{table.Name}' declares constraints that the SQL DDL executor does not support yet.");
        }

        var builder = new StringBuilder();
        builder.Append("CREATE TABLE IF NOT EXISTS ")
               .Append(QualifiedTable(table.Name))
               .Append(" (");

        for (int index = 0; index < table.Columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            CompiledSchemaColumn column = table.Columns[index];
            AppendColumn(builder, column, IsPrimaryKeyColumn(table.PrimaryKey, column.Name));
        }

        return builder.Append(");").ToString();
    }

    internal static string DropTable(string tableName)
        => $"DROP TABLE IF EXISTS {QualifiedTable(tableName)};";

    internal static string AddColumn(string tableName, CompiledSchemaColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        var builder = new StringBuilder();
        builder.Append("ALTER TABLE ")
               .Append(QualifiedTable(tableName))
               .Append(" ADD COLUMN ");
        AppendColumn(builder, column, isPrimaryKey: false);
        return builder.Append(';').ToString();
    }

    internal static string DropColumn(string tableName, string columnName)
        => $"ALTER TABLE {QualifiedTable(tableName)} DROP COLUMN {Identifier(columnName)};";

    internal static string CreateIndex(string tableName, CompiledSchemaIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        var builder = new StringBuilder("CREATE ");
        if (index.IsUnique)
        {
            builder.Append("UNIQUE ");
        }

        builder.Append("INDEX IF NOT EXISTS ")
               .Append(Identifier(index.Name))
               .Append(" ON ")
               .Append(QualifiedTable(tableName))
               .Append(" (");

        for (int columnIndex = 0; columnIndex < index.Columns.Count; columnIndex++)
        {
            if (columnIndex > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Identifier(index.Columns[columnIndex]));
        }

        return builder.Append(");").ToString();
    }

    internal static string DropIndex(string tableName, string indexName)
        => $"DROP INDEX IF EXISTS {Identifier(indexName)} ON {QualifiedTable(tableName)};";

    private static void AppendColumn(StringBuilder builder, CompiledSchemaColumn column, bool isPrimaryKey)
    {
        builder.Append(Identifier(column.Name))
               .Append(' ')
               .Append(TypeName(column));

        if (isPrimaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        builder.Append(column.IsNullable && !isPrimaryKey ? " NULL" : " NOT NULL");
    }

    private static bool IsPrimaryKeyColumn(CompiledSchemaKey? key, string columnName)
    {
        if (key is null)
        {
            return false;
        }

        foreach (string keyColumn in key.Columns)
        {
            if (string.Equals(keyColumn, columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string TypeName(CompiledSchemaColumn column)
    {
        if (!string.IsNullOrWhiteSpace(column.CustomType))
        {
            throw new DatabaseException(
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
            _ => throw new DatabaseException(
                $"Column '{column.Name}' uses database type '{column.Type}', which cannot be emitted as SQL DDL."),
        };
    }

    private static string DecimalTypeName(CompiledSchemaColumn column)
    {
        if (column.Scale is not null && column.Precision is null)
        {
            throw new DatabaseException(
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

    private static string QualifiedTable(string tableName)
        => $"{defaultSchema}.{Identifier(tableName)}";

    private static string Identifier(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!(char.IsLetter(value[0]) || value[0] == '_'))
        {
            throw new DatabaseException($"Schema identifier '{value}' cannot be represented by the SQL dialect.");
        }

        for (int index = 1; index < value.Length; index++)
        {
            if (!(char.IsLetterOrDigit(value[index]) || value[index] == '_'))
            {
                throw new DatabaseException($"Schema identifier '{value}' cannot be represented by the SQL dialect.");
            }
        }

        return value;
    }
}
