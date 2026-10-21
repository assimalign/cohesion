using System;
using System.Collections.Generic;
using System.Globalization;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Validates key declaration shape before catalog publication or index construction.</summary>
internal static class SqlConstraintDeclaration
{
    internal static string Name(string table, SqlConstraintDefinition definition, int ordinal)
        => definition.Name ?? $"{definition.Kind}_{table}_{string.Join('_', definition.Columns)}_{ordinal.ToString(CultureInfo.InvariantCulture)}";

    internal static void ValidateColumns(SqlConstraintDefinition definition, string name)
    {
        if (definition.Kind is not (SqlConstraintKind.PrimaryKey or SqlConstraintKind.Unique or SqlConstraintKind.ForeignKey)) { return; }
        // PostgreSQL parse_utilcmd.c:2768-2781 rejects repeated PK/UNIQUE columns.
        // Refusing repeated FK child columns is deliberately stricter than PostgreSQL.
        ValidateColumnList(definition.Columns, name);
        if (definition.Kind == SqlConstraintKind.ForeignKey)
        {
            // PostgreSQL tablecmds.c:13982-13989 rejects repeated referenced columns (42830).
            ValidateColumnList(definition.ReferencedColumns, name);
        }
    }

    private static void ValidateColumnList(IReadOnlyList<string> declared, string name)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string column in declared)
        {
            if (!columns.Add(column))
            {
                throw new SqlPlanningException("COHSQLE019", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.DuplicateConstraintColumn, column, name));
            }
        }
    }

    // PostgreSQL parse_utilcmd.c:2378 rejects a second PRIMARY KEY declaration (42P16).
    internal static SqlPlanningException MultiplePrimaryKeys(string table, string constraint)
        => new("COHSQLE019", string.Format(CultureInfo.CurrentCulture, ErrorMessages.MultiplePrimaryKeys, constraint, table));
}
