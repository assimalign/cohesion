using System;
using System.Collections.Generic;
using System.Linq;

using Assimalign.Cohesion.Database.Sql.Language.Internal;
using Assimalign.Cohesion.Database.Types;

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

        // SQL identifiers compare without case, as the engine's catalog does.
        StringComparer identifiers = StringComparer.OrdinalIgnoreCase;
        if (current is not null && !identifiers.Equals(current.Name, desired.Name))
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaMigrationPlannerPlanFailure, current.Name, desired.Name));
        }

        if (current is not null && string.Equals(current.Hash, desired.Hash, StringComparison.Ordinal))
        {
            return new SqlSchemaMigrationPlan(current.Hash, desired.Hash, Array.Empty<SqlSchemaMigrationOperation>());
        }

        var operations = new List<SqlSchemaMigrationOperation>();
        IReadOnlyList<CompiledSchemaType> currentTypes = current?.Types ?? Array.Empty<CompiledSchemaType>();

        // Domains first, so the columns of the tables below can be declared with them; a domain the
        // schema no longer declares is dropped last, after the columns that used it.
        PlanAddedDomains(currentTypes, desired.Types, operations, identifiers);
        PlanTables(current?.Tables ?? Array.Empty<CompiledSchemaTable>(), desired.Tables, operations, identifiers,
            currentTypes, desired.Types);
        PlanDroppedDomains(currentTypes, desired.Types, operations, identifiers);
        EnsureSupportedMetadataIsUnchanged(current, desired);

        SqlSchemaMigrationOperation? destructive = operations.FirstOrDefault(
            operation => operation.Safety == SqlSchemaMigrationSafety.Destructive);
        if (destructive is not null && !desired.AllowsDestructiveChanges)
        {
            string qualifiedName = destructive.ParentName is null
                ? destructive.ObjectName
                : $"{destructive.ParentName}.{destructive.ObjectName}";
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlSchemaMigrationPlannerPlanFailure2, destructive.Kind, qualifiedName));
        }

        return new SqlSchemaMigrationPlan(current?.Hash, desired.Hash, operations);
    }

    private static void PlanTables(
        IReadOnlyList<CompiledSchemaTable> current,
        IReadOnlyList<CompiledSchemaTable> desired,
        List<SqlSchemaMigrationOperation> operations,
        StringComparer identifiers,
        IReadOnlyList<CompiledSchemaType> previousTypes, IReadOnlyList<CompiledSchemaType> desiredTypes)
    {
        var oldDomains = previousTypes.ToDictionary(type => type.Name, identifiers);
        var newDomains = desiredTypes.ToDictionary(type => type.Name, identifiers);
        var oldTables = current.ToDictionary(table => table.Name, identifiers);
        var newTables = desired.ToDictionary(table => table.Name, identifiers);

        // Remove references before their target indexes, columns, or tables.
        foreach (CompiledSchemaTable oldTable in current.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            newTables.TryGetValue(oldTable.Name, out CompiledSchemaTable? newTable);
            foreach (CompiledSchemaConstraint constraint in oldTable.Constraints)
            {
                CompiledSchemaConstraint? replacement = newTable?.Constraints.FirstOrDefault(value => identifiers.Equals(value.Name, constraint.Name));
                if (replacement is null || !ConstraintEquals(constraint, replacement, identifiers))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.DropConstraint, SqlSchemaMigrationSafety.Safe,
                        constraint.Name, oldTable.Name, constraint: constraint));
                }
            }
        }

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
                if (!newIndexes.TryGetValue(oldIndex.Name, out CompiledSchemaIndex? newIndex) || !IndexEquals(oldIndex, newIndex, identifiers) ||
                    oldIndex.Predicate is not null && RetypesColumn(oldTable, newTable, identifiers, oldDomains, newDomains))
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
                    table: new CompiledSchemaTable(newTable.Name, newTable.RowType, newTable.Columns,
                        newTable.PrimaryKey, newTable.Indexes, Array.Empty<CompiledSchemaConstraint>())));
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

            var oldColumns = oldTable.Columns.Select(column => EffectiveColumn(column, oldDomains)).ToDictionary(column => column.Name, identifiers);
            var newColumns = newTable.Columns.Select(column => EffectiveColumn(column, newDomains)).ToDictionary(column => column.Name, identifiers);
            foreach (CompiledSchemaColumn declaredColumn in newTable.Columns)
            {
                var column = newColumns[declaredColumn.Name];
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
                        ColumnChangeSafety(previous, column, oldDomains, newDomains),
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
                !KeyEquals(oldTable.PrimaryKey, newTable.PrimaryKey, identifiers))
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
                if (!oldIndexes.TryGetValue(index.Name, out CompiledSchemaIndex? previous) || !IndexEquals(previous, index, identifiers) ||
                    index.Predicate is not null && RetypesColumn(oldTable, newTable, identifiers, oldDomains, newDomains))
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

        // All tables and unique indexes exist before any FK is bound. Cycles and
        // self-references therefore need no special ordering or disabled checks.
        foreach (CompiledSchemaTable newTable in desired.OrderBy(table => table.Name, StringComparer.Ordinal))
        {
            oldTables.TryGetValue(newTable.Name, out CompiledSchemaTable? oldTable);
            foreach (CompiledSchemaConstraint constraint in newTable.Constraints)
            {
                CompiledSchemaConstraint? previous = oldTable?.Constraints.FirstOrDefault(value => identifiers.Equals(value.Name, constraint.Name));
                if (previous is null || !ConstraintEquals(previous, constraint, identifiers))
                {
                    operations.Add(new SqlSchemaMigrationOperation(
                        SqlSchemaMigrationOperationKind.AddConstraint, SqlSchemaMigrationSafety.Safe,
                        constraint.Name, newTable.Name, constraint: constraint));
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

    // A domain's immutable storage facets live on its declaration rather than every
    // referring column. Diff those effective facets so a same-name domain change
    // carries an AlterColumn payload and cannot merely advance the applied hash.
    private static CompiledSchemaColumn EffectiveColumn(CompiledSchemaColumn column,
        IReadOnlyDictionary<string, CompiledSchemaType> domains)
        => column.CustomType is { } name && domains.TryGetValue(name, out var domain)
            ? column with { Type = domain.StorageType, Precision = domain.Precision, Scale = domain.Scale }
            : column;

    // Metadata-only changes retain the existing planner policy. A TYPE conversion
    // is safe only when every source value has an exact representation at the target.
    // Unknown application domains/casts are deliberately outside this closed proof.
    private static SqlSchemaMigrationSafety ColumnChangeSafety(CompiledSchemaColumn previous, CompiledSchemaColumn desired,
        IReadOnlyDictionary<string, CompiledSchemaType> oldDomains, IReadOnlyDictionary<string, CompiledSchemaType> newDomains)
    {
        if (previous.IsNullable != desired.IsNullable || previous.DefaultLiteral != desired.DefaultLiteral ||
            !TryStorage(previous, oldDomains, out var source) || !TryStorage(desired, newDomains, out var target))
        { return SqlSchemaMigrationSafety.Destructive; }
        bool typeChanged = previous.Type != desired.Type || previous.MaxLength != desired.MaxLength ||
            previous.Precision != desired.Precision || previous.Scale != desired.Scale ||
            !string.Equals(previous.CustomType, desired.CustomType, StringComparison.OrdinalIgnoreCase);
        if (!typeChanged) { return SqlSchemaMigrationSafety.Destructive; }
        if (target.MaxLength is { } length && (source.MaxLength is null || length < source.MaxLength))
        { return SqlSchemaMigrationSafety.Destructive; }
        bool exact;
        if (source.Type == target.Type)
        {
            exact = target.Type != DatabaseType.Decimal
                ? source.Scale == target.Scale
                : target.Precision is null || source.Precision is { } precision &&
                    target.Scale >= source.Scale && target.Precision - target.Scale >= precision - source.Scale;
        }
        else
        {
            exact = (source.Type, target.Type) switch
            {
                (DatabaseType.Int8, DatabaseType.Int16 or DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64) => true,
                (DatabaseType.Int16, DatabaseType.Int32 or DatabaseType.Int64 or DatabaseType.Float32 or DatabaseType.Float64) => true,
                (DatabaseType.Int32, DatabaseType.Int64 or DatabaseType.Float64) => true,
                (DatabaseType.Float32, DatabaseType.Float64) => true,
                _ => false,
            };
        }
        return exact ? SqlSchemaMigrationSafety.Safe : SqlSchemaMigrationSafety.Destructive;
    }

    private static bool TryStorage(CompiledSchemaColumn column, IReadOnlyDictionary<string, CompiledSchemaType> domains,
        out DatabaseTypeInfo storage)
    {
        storage = new(column.Type, column.MaxLength, column.Precision, column.Scale);
        if (column.CustomType is not { } text) { return true; }
        // Even a schema-declared domain can retain an application's registered casts.
        // This assembly cannot prove those conversions preserve every source value.
        if (domains.ContainsKey(text)) { return false; }
        if (!SqlTypeModifiers.TrySplit(text, out string name, out int? first, out int? second) ||
            SqlTypeModifiers.StorageType(name) is not { } type) { return false; }
        storage = type switch
        {
            DatabaseType.String or DatabaseType.Character or DatabaseType.Binary => new(type, first ?? (type == DatabaseType.Character ? 1 : null)),
            DatabaseType.Decimal => new(type, null, first, SqlTypeModifiers.NormalizeScale(type, first, second)),
            _ => new(column.Type),
        };
        return true;
    }

    /// <summary>
    /// Plans each domain the desired schema declares that the current one does not, or declares over
    /// another storage type, precision or scale (phase E3). A changed domain is dropped and added
    /// again; the columns declared with it change through their own steps.
    /// </summary>
    private static void PlanAddedDomains(
        IReadOnlyList<CompiledSchemaType> current,
        IReadOnlyList<CompiledSchemaType> desired,
        List<SqlSchemaMigrationOperation> operations,
        StringComparer identifiers)
    {
        foreach (CompiledSchemaType domain in desired.OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            CompiledSchemaType? previous = current.FirstOrDefault(type => identifiers.Equals(type.Name, domain.Name));
            if (previous is not null && DomainEquals(previous, domain))
            {
                continue;
            }

            if (previous is not null)
            {
                operations.Add(new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.DropDomain, SqlSchemaMigrationSafety.Safe, previous.Name, domain: previous));
            }

            operations.Add(new SqlSchemaMigrationOperation(
                SqlSchemaMigrationOperationKind.AddDomain, SqlSchemaMigrationSafety.Safe, domain.Name, domain: domain));
        }
    }

    /// <summary>Plans each domain the current schema declares that the desired one does not.</summary>
    private static void PlanDroppedDomains(
        IReadOnlyList<CompiledSchemaType> current,
        IReadOnlyList<CompiledSchemaType> desired,
        List<SqlSchemaMigrationOperation> operations,
        StringComparer identifiers)
    {
        foreach (CompiledSchemaType domain in current.OrderByDescending(type => type.Name, StringComparer.Ordinal))
        {
            if (!desired.Any(type => identifiers.Equals(type.Name, domain.Name)))
            {
                operations.Add(new SqlSchemaMigrationOperation(
                    SqlSchemaMigrationOperationKind.DropDomain, SqlSchemaMigrationSafety.Safe, domain.Name, domain: domain));
            }
        }
    }

    private static bool DomainEquals(CompiledSchemaType left, CompiledSchemaType right)
        => left.StorageType == right.StorageType && left.Precision == right.Precision && left.Scale == right.Scale;

    // Predicate fragments intentionally have no parser in this assembly. Conservatively
    // rebuild every partial index on a retyped table rather than guessing dependencies.
    private static bool RetypesColumn(CompiledSchemaTable previous, CompiledSchemaTable desired, StringComparer identifiers,
        IReadOnlyDictionary<string, CompiledSchemaType> oldDomains, IReadOnlyDictionary<string, CompiledSchemaType> newDomains)
        => desired.Columns.Any(declared =>
        {
            if (previous.Columns.FirstOrDefault(old => identifiers.Equals(old.Name, declared.Name)) is not { } prior) { return false; }
            var column = EffectiveColumn(declared, newDomains);
            var old = EffectiveColumn(prior, oldDomains);
            return old.Type != column.Type || old.MaxLength != column.MaxLength || old.Precision != column.Precision || old.Scale != column.Scale ||
                !identifiers.Equals(old.CustomType, column.CustomType);
        });

    private static bool IndexEquals(
        CompiledSchemaIndex left,
        CompiledSchemaIndex right,
        StringComparer identifiers)
        => identifiers.Equals(left.Name, right.Name)
        && left.IsUnique == right.IsUnique
        && left.KeyOrders.SequenceEqual(right.KeyOrders)
        && string.Equals(left.Predicate, right.Predicate, StringComparison.Ordinal)
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
        && string.Equals(left.CustomType, right.CustomType, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.DefaultLiteral, right.DefaultLiteral, StringComparison.Ordinal);

    private static bool KeyEquals(
        CompiledSchemaKey? left,
        CompiledSchemaKey? right,
        StringComparer identifiers)
        => ReferenceEquals(left, right)
        || (left is not null
            && right is not null
            && identifiers.Equals(left.Name, right.Name)
            && left.Columns.SequenceEqual(right.Columns, identifiers));

    private static bool ConstraintEquals(CompiledSchemaConstraint first, CompiledSchemaConstraint second, StringComparer identifiers)
        => identifiers.Equals(first.Name, second.Name)
        && first.Kind == second.Kind
        && first.OnDelete == second.OnDelete
        && first.OnUpdate == second.OnUpdate
        && (first.Kind == CompiledSchemaConstraintKind.Check || first.Columns.SequenceEqual(second.Columns, identifiers))
        && identifiers.Equals(first.ReferencedObject, second.ReferencedObject)
        && first.ReferencedColumns.SequenceEqual(second.ReferencedColumns, identifiers)
        && string.Equals(first.Expression?.CanonicalText, second.Expression?.CanonicalText, StringComparison.Ordinal);

    private static void EnsureSupportedMetadataIsUnchanged(SqlCompiledSchema? current, SqlCompiledSchema desired)
    {
        if (current is null)
        {
            if (desired.Principals.Count > 0)
            {
                throw UnsupportedMetadata(desired.Name);
            }

            return;
        }

        if (!PrincipalsEqual(current.Principals, desired.Principals))
        {
            throw UnsupportedMetadata(desired.Name);
        }
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
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.PlannerUnsupportedMetadataFailure, schemaName));
}
