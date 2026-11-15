using System;
using System.Collections.Generic;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Sql.Schema.Internal;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Renders the SQL engine's supported compiled-schema operations into stable text.
/// </summary>
internal static class SqlSchemaStatementRenderer
{
    private const string defaultSchema = SqlPlanner.DefaultSchema;

    internal static string CreateTable(CompiledSchemaTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

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
            AppendColumn(builder, column, IsPrimaryKeyColumn(table.PrimaryKey, column.Name),
                emitPrimaryKey: table.PrimaryKey?.Columns.Count == 1);
        }

        if (table.PrimaryKey is { Columns.Count: > 1 } primaryKey)
        {
            builder.Append(", PRIMARY KEY (");
            AppendColumns(builder, primaryKey.Columns);
            builder.Append(')');
        }

        foreach (CompiledSchemaConstraint constraint in table.Constraints)
        {
            builder.Append(", ");
            AppendConstraint(builder, constraint);
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

    internal static string AddConstraint(string tableName, CompiledSchemaConstraint constraint)
    {
        var builder = new StringBuilder($"ALTER TABLE {QualifiedTable(tableName)} ADD ");
        AppendConstraint(builder, constraint);
        return builder.Append(';').ToString();
    }

    internal static string DropConstraint(string tableName, string constraintName)
        => $"ALTER TABLE {QualifiedTable(tableName)} DROP CONSTRAINT {Identifier(constraintName)};";

    private static void AppendConstraint(StringBuilder builder, CompiledSchemaConstraint constraint)
    {
        builder.Append("CONSTRAINT ").Append(Identifier(constraint.Name)).Append(' ');
        switch (constraint.Kind)
        {
            case CompiledSchemaConstraintKind.Reference:
                builder.Append("FOREIGN KEY (");
                AppendColumns(builder, constraint.Columns);
                builder.Append(") REFERENCES ").Append(QualifiedTable(constraint.ReferencedObject!)).Append(" (");
                AppendColumns(builder, constraint.ReferencedColumns);
                builder.Append(") ON DELETE ").Append(constraint.OnDelete switch
                {
                    CompiledSchemaReferentialAction.Restrict => "RESTRICT",
                    CompiledSchemaReferentialAction.Cascade => "CASCADE",
                    _ => throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RendererAppendConstraintDatabase, constraint.Name)),
                });
                builder.Append(" ON UPDATE ").Append(constraint.OnUpdate switch
                {
                    CompiledSchemaReferentialAction.Restrict => "RESTRICT",
                    CompiledSchemaReferentialAction.Cascade => "CASCADE",
                    CompiledSchemaReferentialAction.NoAction => "NO ACTION",
                    CompiledSchemaReferentialAction.SetNull => "SET NULL",
                    CompiledSchemaReferentialAction.SetDefault => "SET DEFAULT",
                    _ => throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RendererAppendConstraintDatabase, constraint.Name)),
                });
                break;
            case CompiledSchemaConstraintKind.Check:
                string expression = constraint.Expression?.CanonicalText
                    ?? throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RendererAppendConstraintDatabase2, constraint.Name));
                builder.Append("CHECK (").Append(expression).Append(')');
                break;
            default:
                throw new DatabaseException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.RendererAppendConstraintDatabase3, constraint.Name));
        }
    }

    private static void AppendColumns(StringBuilder builder, IReadOnlyList<string> columns)
    {
        for (int index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }
            builder.Append(Identifier(columns[index]));
        }
    }

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

            builder.Append(Identifier(index.Columns[columnIndex])).Append(index.KeyOrders[columnIndex] switch
            {
                CompiledSchemaIndexKeyOrder.AscendingNullsLast => " NULLS LAST",
                CompiledSchemaIndexKeyOrder.DescendingNullsLast => " DESC",
                CompiledSchemaIndexKeyOrder.DescendingNullsFirst => " DESC NULLS FIRST",
                _ => "",
            });
        }

        builder.Append(')');
        if (index.Predicate is { } predicate) { builder.Append(" WHERE ").Append(SqlPersistedExpression.Canonicalize(SqlPersistedExpression.Load(predicate, index.Name), index.Name)); }
        return builder.Append(';').ToString();
    }

    internal static string DropIndex(string tableName, string indexName)
        => $"DROP INDEX IF EXISTS {Identifier(indexName)} ON {QualifiedTable(tableName)};";

    private static void AppendColumn(StringBuilder builder, CompiledSchemaColumn column, bool isPrimaryKey, bool emitPrimaryKey = true)
    {
        builder.Append(Identifier(column.Name))
               .Append(' ')
               .Append(TypeName(column));

        if (isPrimaryKey && emitPrimaryKey)
        {
            builder.Append(" PRIMARY KEY");
        }

        builder.Append(column.IsNullable && !isPrimaryKey ? " NULL" : " NOT NULL");
        if (column.DefaultLiteral is not null)
        {
            builder.Append(" DEFAULT ").Append(SqlSchemaProvisioner.CanonicalDefault(column.DefaultLiteral) ?? "NULL");
        }
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
        => SqlMigrationLowering.TypeName(column);

    private static string QualifiedTable(string tableName)
        => $"{defaultSchema}.{Identifier(tableName)}";

    private static string Identifier(string value)
        => SqlMigrationLowering.Identifier(value);
}
