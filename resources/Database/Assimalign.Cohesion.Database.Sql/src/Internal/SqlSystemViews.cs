using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlSystemViewDefinition(string Schema, string Name, IReadOnlyList<SqlCatalogColumn> Columns);

/// <summary>Reserved virtual relations and their typed, ordered column contracts.</summary>
internal static class SqlSystemViews
{
    private static readonly SqlSystemViewDefinition[] Views =
    [
        Iso("TABLES", Text("TABLE_CATALOG"), Text("TABLE_SCHEMA"), Text("TABLE_NAME"), Text("TABLE_TYPE")),
        Iso("COLUMNS", Text("TABLE_CATALOG"), Text("TABLE_SCHEMA"), Text("TABLE_NAME"), Text("COLUMN_NAME"),
            Number("ORDINAL_POSITION"), Text("COLUMN_DEFAULT", true), Text("IS_NULLABLE"), Text("DATA_TYPE"),
            Number("CHARACTER_MAXIMUM_LENGTH", true), Number("CHARACTER_OCTET_LENGTH", true),
            Number("NUMERIC_PRECISION", true), Number("NUMERIC_PRECISION_RADIX", true),
            Number("NUMERIC_SCALE", true), Number("DATETIME_PRECISION", true)),
        Iso("TABLE_CONSTRAINTS", Text("CONSTRAINT_CATALOG"), Text("CONSTRAINT_SCHEMA"), Text("CONSTRAINT_NAME"),
            Text("TABLE_CATALOG"), Text("TABLE_SCHEMA"), Text("TABLE_NAME"), Text("CONSTRAINT_TYPE"),
            Text("IS_DEFERRABLE"), Text("INITIALLY_DEFERRED")),
        Iso("KEY_COLUMN_USAGE", Text("CONSTRAINT_CATALOG"), Text("CONSTRAINT_SCHEMA"), Text("CONSTRAINT_NAME"),
            Text("TABLE_CATALOG"), Text("TABLE_SCHEMA"), Text("TABLE_NAME"), Text("COLUMN_NAME"), Number("ORDINAL_POSITION")),
        Iso("REFERENTIAL_CONSTRAINTS", Text("CONSTRAINT_CATALOG"), Text("CONSTRAINT_SCHEMA"), Text("CONSTRAINT_NAME"),
            Text("UNIQUE_CONSTRAINT_CATALOG", true), Text("UNIQUE_CONSTRAINT_SCHEMA", true), Text("UNIQUE_CONSTRAINT_NAME", true),
            Text("MATCH_OPTION"), Text("UPDATE_RULE"), Text("DELETE_RULE")),
        Iso("CHECK_CONSTRAINTS", Text("CONSTRAINT_CATALOG"), Text("CONSTRAINT_SCHEMA"), Text("CONSTRAINT_NAME"), Text("CHECK_CLAUSE")),
        Cohesion("INDEXES", Text("TABLE_CATALOG"), Text("TABLE_SCHEMA"), Text("TABLE_NAME"), Text("INDEX_NAME"),
            Text("COLUMN_NAME"), Number("ORDINAL_POSITION"), Text("IS_UNIQUE"), Text("IS_PRIMARY_KEY")),
        Cohesion("OBJECT_OWNERSHIP", Text("TABLE_CATALOG"), Text("TABLE_SCHEMA"), Text("TABLE_NAME"),
            Text("OBJECT_TYPE"), Text("OBJECT_NAME"), Text("OWNER"), Text("OWNING_SCHEMA", true)),
    ];

    internal static SqlSystemViewDefinition? Find(SqlTableReference reference)
    {
        foreach (var view in Views)
        {
            if (string.Equals(view.Schema, reference.SchemaName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(view.Name, reference.TableName, StringComparison.OrdinalIgnoreCase))
            {
                return view;
            }
        }

        return null;
    }

    // Validate before ordinary table lookup, IF [NOT] EXISTS shortcuts, or the
    // session's transaction-mode check so every supported mutation has one diagnostic.
    internal static void EnsureReadOnly(SqlQueryExpression expression)
    {
        SqlTableReference? target = expression switch
        {
            SqlInsertExpression insert => insert.Table,
            SqlUpdateExpression update => update.Table,
            SqlDeleteExpression delete => delete.Table,
            SqlCreateTableExpression create => create.Table,
            SqlDropTableExpression drop => drop.Table,
            SqlAlterTableExpression alter => alter.Table,
            SqlCreateIndexExpression create => create.Table,
            SqlDropIndexExpression drop => drop.Table,
            _ => null,
        };

        if (target is not null && Find(target) is { } view)
        {
            throw new DatabaseException($"System view '{view.Schema}.{view.Name}' is read-only.");
        }
    }

    private static SqlSystemViewDefinition Iso(string name, params SqlCatalogColumn[] columns)
        => new("INFORMATION_SCHEMA", name, Array.AsReadOnly(columns));

    private static SqlSystemViewDefinition Cohesion(string name, params SqlCatalogColumn[] columns)
        => new("COHESION_SCHEMA", name, Array.AsReadOnly(columns));

    private static SqlCatalogColumn Text(string name, bool nullable = false)
        => new(name, new DatabaseTypeInfo(DatabaseType.String), nullable);

    private static SqlCatalogColumn Number(string name, bool nullable = false)
        => new(name, new DatabaseTypeInfo(DatabaseType.Int64), nullable);
}
