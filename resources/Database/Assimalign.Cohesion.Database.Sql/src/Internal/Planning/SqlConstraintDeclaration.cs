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
        // PostgreSQL parse_utilcmd.c:2773-2779 rejects repeated key columns instead of deduplicating them.
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string column in definition.Columns)
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
