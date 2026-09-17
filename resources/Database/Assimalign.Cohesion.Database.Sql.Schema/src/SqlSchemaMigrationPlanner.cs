using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Creates deterministic migration plans by diffing compiled schemas.</summary>
public static class SqlSchemaMigrationPlanner
{
    /// <summary>Diffs a current schema against the desired schema.</summary>
    /// <param name="current">The current schema, or null for an empty catalog.</param>
    /// <param name="desired">The desired schema.</param>
    /// <returns>An ordered migration plan.</returns>
    /// <exception cref="SqlSchemaMigrationException">The schemas are incompatible or a destructive operation is not allowed.</exception>
    public static SqlSchemaMigrationPlan Plan(SqlCompiledSchema? current, SqlCompiledSchema desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        StringComparer identifiers = desired.Model == EngineModel.Sql
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (current is not null && current.Model != desired.Model)
        {
            throw new SqlSchemaMigrationException(
                $"Schema '{desired.Name}' targets {desired.Model}, but the catalog schema targets {current.Model}.");
        }

        if (current is not null && !identifiers.Equals(current.Name, desired.Name))
        {
            throw new SqlSchemaMigrationException(
                $"Cannot migrate database '{current.Name}' with schema '{desired.Name}'.");
        }

        if (current is not null && string.Equals(current.Hash, desired.Hash, StringComparison.Ordinal))
        {
            return new SqlSchemaMigrationPlan(current.Hash, desired.Hash, Array.Empty<SqlSchemaMigrationOperation>());
        }

        var operations = new List<SqlSchemaMigrationOperation>();
        PlanTables(current?.Tables ?? Array.Empty<CompiledSchemaTable>(), desired.Tables, operations, identifiers);
        EnsureSupportedMetadataIsUnchanged(current, desired);

        SqlSchemaMigrationOperation? destructive = operations.FirstOrDefault(
            operation => operation.Safety == SqlSchemaMigrationSafety.Destructive);
        if (destructive is not null && !desired.AllowsDestructiveChanges)
        {
            string qualifiedName = destructive.ParentName is null
                ? destructive.ObjectName
                : $"{destructive.ParentName}.{destructive.ObjectName}";
            throw new SqlSchemaMigrationException(
                $"Destructive migration step '{destructive.Kind}' for '{qualifiedName}' was refused. " +
                "Call schema.AllowDestructiveChanges() in the database declaration to opt in.");
        }

        return new SqlSchemaMigrationPlan(current?.Hash, desired.Hash, operations);
    }

    private static void PlanTables(
        IReadOnlyList<CompiledSchemaTable> current,
        IReadOnlyList<CompiledSchemaTable> desired,
        List<SqlSchemaMigrationOperation> operations,
        StringComparer identifiers)
    {
        var oldTables = current.ToDictionary(table => table.Name, identifiers);
        var newTables = desired.ToDictionary(table => table.Name, identifiers);

        // Remove indexes first so later destructive column/table operations never leave dangling metadata.
        foreach (CompiledSchemaTable oldTable in current.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            if (!newTables.TryGetValue(oldTable.Name, out CompiledSchemaTable? newTable))
            {
                continue;
            }

            var newIndexes = newTable.Indexes.ToDictionary(index => index.Name, identifiers);
            foreach (CompiledSchemaIndex oldIndex in oldTable.Indexes.OrderBy(index => index.Name, StringComparer.Ordinal))
            {
                if (!newIndexes.TryGetValue(oldIndex.Name, out CompiledSchemaIndex? newIndex) || !IndexEquals(oldIndex, newIndex, identifiers))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.DropIndex,
                        SqlSchemaMigrationSafety.Safe,
                        oldIndex.Name,
                        oldTable.Name));
                }
            }
        }

        foreach (CompiledSchemaTable newTable in desired.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            if (!oldTables.TryGetValue(newTable.Name, out CompiledSchemaTable? oldTable))
            {
                operations.Add(new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.AddTable,
                    SqlSchemaMigrationSafety.Safe,
                    newTable.Name,
                    table: newTable));
                foreach (CompiledSchemaIndex index in newTable.Indexes.OrderBy(value => value.Name, StringComparer.Ordinal))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.AddIndex,
                        SqlSchemaMigrationSafety.Safe,
                        index.Name,
                        newTable.Name,
                        index: index));
                }

                continue;
            }

            var oldColumns = oldTable.Columns.ToDictionary(column => column.Name, identifiers);
            var newColumns = newTable.Columns.ToDictionary(column => column.Name, identifiers);
            foreach (CompiledSchemaColumn column in newTable.Columns)
            {
                if (!oldColumns.TryGetValue(column.Name, out CompiledSchemaColumn? previous))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.AddColumn,
                        column.IsNullable ? SqlSchemaMigrationSafety.Safe : SqlSchemaMigrationSafety.Destructive,
                        column.Name,
                        newTable.Name,
                        column: column));
                }
                else if (!ColumnEquals(previous, column, identifiers))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.AlterColumn,
                        SqlSchemaMigrationSafety.Destructive,
                        column.Name,
                        newTable.Name,
                        column: column,
                        previousColumn: previous));
                }
            }

            IEnumerable<string> resultingColumnOrder = oldTable.Columns
                .Where(column => newColumns.ContainsKey(column.Name))
                .Select(column => column.Name)
                .Concat(newTable.Columns
                    .Where(column => !oldColumns.ContainsKey(column.Name))
                    .Select(column => column.Name));
            bool columnOrderChanged = !resultingColumnOrder.SequenceEqual(
                newTable.Columns.Select(column => column.Name),
                identifiers);
            if (columnOrderChanged ||
                !KeyEquals(oldTable.PrimaryKey, newTable.PrimaryKey, identifiers) ||
                !ConstraintsEqual(oldTable.Constraints, newTable.Constraints, identifiers))
            {
                operations.Add(new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.AlterTable,
                    SqlSchemaMigrationSafety.Destructive,
                    newTable.Name,
                    table: newTable));
            }

            var oldIndexes = oldTable.Indexes.ToDictionary(index => index.Name, identifiers);
            foreach (CompiledSchemaIndex index in newTable.Indexes.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (!oldIndexes.TryGetValue(index.Name, out CompiledSchemaIndex? previous) || !IndexEquals(previous, index, identifiers))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.AddIndex,
                        SqlSchemaMigrationSafety.Safe,
                        index.Name,
                        newTable.Name,
                        index: index));
                }
            }

            foreach (CompiledSchemaColumn column in oldTable.Columns.OrderBy(value => value.Name, StringComparer.Ordinal))
            {
                if (!newColumns.ContainsKey(column.Name))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.DropColumn,
                        SqlSchemaMigrationSafety.Destructive,
                        column.Name,
                        oldTable.Name));
                }
            }
        }

        foreach (CompiledSchemaTable oldTable in current.OrderByDescending(table => table.Name, StringComparer.Ordinal))
        {
            if (!newTables.ContainsKey(oldTable.Name))
            {
                operations.Add(new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.DropTable,
                    SqlSchemaMigrationSafety.Destructive,
                    oldTable.Name));
            }
        }
    }

    private static bool IndexEquals(
        CompiledSchemaIndex left,
        CompiledSchemaIndex right,
        StringComparer identifiers)
        => identifiers.Equals(left.Name, right.Name)
        && left.IsUnique == right.IsUnique
        && left.Columns.SequenceEqual(right.Columns, identifiers);

    private static bool ColumnEquals(
        CompiledSchemaColumn left,
        CompiledSchemaColumn right,
        StringComparer identifiers)
        => identifiers.Equals(left.Name, right.Name)
        && left.Type == right.Type
        && left.IsNullable == right.IsNullable
        && left.MaxLength == right.MaxLength
        && left.Precision == right.Precision
        && left.Scale == right.Scale
        && string.Equals(left.CustomType, right.CustomType, StringComparison.Ordinal);

    private static bool KeyEquals(
        CompiledSchemaKey? left,
        CompiledSchemaKey? right,
        StringComparer identifiers)
        => ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && identifiers.Equals(left.Name, right.Name)
            && left.Columns.SequenceEqual(right.Columns, identifiers));

    private static bool ConstraintsEqual(
        IReadOnlyList<CompiledSchemaConstraint> left,
        IReadOnlyList<CompiledSchemaConstraint> right,
        StringComparer identifiers)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            CompiledSchemaConstraint first = left[index];
            CompiledSchemaConstraint second = right[index];
            if (!identifiers.Equals(first.Name, second.Name)
                || first.Kind != second.Kind
                || !first.Columns.SequenceEqual(second.Columns, identifiers)
                || !identifiers.Equals(first.ReferencedObject, second.ReferencedObject)
                || !first.ReferencedColumns.SequenceEqual(second.ReferencedColumns, identifiers)
                || !string.Equals(first.Expression?.CanonicalText, second.Expression?.CanonicalText, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void EnsureSupportedMetadataIsUnchanged(SqlCompiledSchema? current, SqlCompiledSchema desired)
    {
        if (current is null)
        {
            if (desired.Types.Count > 0 || desired.Functions.Count > 0 || desired.Triggers.Count > 0 ||
                desired.Principals.Count > 0 || desired.Extensions.Count > 0)
            {
                throw UnsupportedMetadata(desired.Name);
            }

            return;
        }

        if (!current.Types.SequenceEqual(desired.Types) ||
            !FunctionsEqual(current.Functions, desired.Functions) ||
            !current.Triggers.SequenceEqual(desired.Triggers) ||
            !PrincipalsEqual(current.Principals, desired.Principals) ||
            !current.Extensions.SequenceEqual(desired.Extensions))
        {
            throw UnsupportedMetadata(desired.Name);
        }
    }

    private static bool FunctionsEqual(
        IReadOnlyList<CompiledSchemaFunction> left,
        IReadOnlyList<CompiledSchemaFunction> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            CompiledSchemaFunction first = left[index];
            CompiledSchemaFunction second = right[index];
            if (!string.Equals(first.Name, second.Name, StringComparison.Ordinal) ||
                first.ResultType != second.ResultType ||
                !string.Equals(first.CustomResultType, second.CustomResultType, StringComparison.Ordinal) ||
                first.Body != second.Body ||
                !first.Parameters.SequenceEqual(second.Parameters))
            {
                return false;
            }
        }

        return true;
    }

    private static bool PrincipalsEqual(
        IReadOnlyList<CompiledSchemaPrincipal> left,
        IReadOnlyList<CompiledSchemaPrincipal> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int index = 0; index < left.Count; index++)
        {
            CompiledSchemaPrincipal first = left[index];
            CompiledSchemaPrincipal second = right[index];
            if (!string.Equals(first.Name, second.Name, StringComparison.Ordinal) ||
                first.Grants.Count != second.Grants.Count)
            {
                return false;
            }

            for (int grantIndex = 0; grantIndex < first.Grants.Count; grantIndex++)
            {
                CompiledSchemaGrant firstGrant = first.Grants[grantIndex];
                CompiledSchemaGrant secondGrant = second.Grants[grantIndex];
                if (firstGrant.Permission != secondGrant.Permission ||
                    !firstGrant.Objects.SequenceEqual(secondGrant.Objects, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static SqlSchemaMigrationException UnsupportedMetadata(string schemaName)
        => new(
            $"Schema '{schemaName}' changes custom types, functions, triggers, principals, or model extensions, " +
            "but the shipped migration statement models cannot apply those metadata dimensions yet.");
}
