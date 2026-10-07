using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    private async Task CreateConstrainedTableAsync(SqlCreateTablePlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // Every DEFAULT must convert to its column before anything is reserved, as ADD COLUMN
        // requires: a default the column cannot store would otherwise be published and fail
        // every later INSERT that omits the column.
        foreach (var column in plan.Columns)
        {
            if (column.DefaultLiteral is not null)
            {
                ResolveDefault(column, SqlPersistedExpression.LoadDefaultValue(column.DefaultLiteral,
                    $"DEFAULT of column '{column.Name}' on table '{plan.Schema}.{plan.Name}'"));
            }
        }

        var provisional = new SqlCatalogTable(0, plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey);
        var constraints = BindConstraints(provisional, plan.Constraints);
        await LockReferencedTablesAsync(constraints, statement, cancellationToken).ConfigureAwait(false);
        // Rebind after waiting: a parent definition might have changed while acquiring its lock.
        constraints = BindConstraints(provisional, plan.Constraints);
        // Durable, but not a self-commit the session counts (#1272): the reservation persists only
        // the object-id counter, and the table stays invisible until the publish.
        var table = await _catalog.ReserveTableAsync(plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey, constraints,
            statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
            statement.ProvisioningSchema, cancellationToken).ConfigureAwait(false);

        // Bind the persisted definitions of the version about to be published, from their
        // stored text, so no write to the new table ever parses them.
        _definitions.Get(table);
        await PublishConstrainedTableAsync(table, plan.Constraints, statement, cancellationToken, replaceExisting: false).ConfigureAwait(false);
    }

    private async Task LockReferencedTablesAsync(IReadOnlyList<SqlCatalogConstraint> constraints, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var parents = new SortedSet<ulong>();
        foreach (var constraint in constraints.Where(c => c.Kind == SqlCatalogConstraintKind.Reference))
        {
            if (_catalog.TryGetTable(constraint.ReferencedSchema!, constraint.ReferencedTable!, out var parent))
            {
                parents.Add(parent.ObjectId);
            }
        }

        foreach (ulong parent in parents)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence, LockResource.Object(parent),
                LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        }
    }

    // Empty CREATE trees and ALTER backfills are durable before the single catalog
    // publish. A crash can leak unpublished tree pages, but cannot expose a table
    // whose declared uniqueness has no enforcing index.
    private async Task PublishConstrainedTableAsync(SqlCatalogTable table, IReadOnlyList<SqlConstraintDefinition> definitions,
        SqlStatementContext statement, CancellationToken cancellationToken, bool replaceExisting)
    {
        var indexes = new List<SqlCatalogIndex>();
        bool primaryCreated = false;
        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i];
            if (definition.Kind is not (SqlConstraintKind.Unique or SqlConstraintKind.PrimaryKey))
            {
                continue;
            }

            if (definition.Kind == SqlConstraintKind.PrimaryKey && primaryCreated)
            {
                continue;
            }

            if (definition.Kind == SqlConstraintKind.PrimaryKey)
            {
                primaryCreated = true;
            }

            var columns = definition.Kind == SqlConstraintKind.PrimaryKey ? table.PrimaryKeyColumns : definition.Columns;
            indexes.Add(new SqlCatalogIndex(table.ObjectId, ConstraintName(table, definition, i), columns, true, table.Owner, table.OwningSchema, isPrimaryKey: definition.Kind == SqlConstraintKind.PrimaryKey));
        }
        if (!replaceExisting && !primaryCreated && table.PrimaryKeyColumns.Count > 0)
        {
            indexes.Add(new SqlCatalogIndex(table.ObjectId, $"PrimaryKey_{table.Name}", table.PrimaryKeyColumns, true, table.Owner, table.OwningSchema, isPrimaryKey: true));
        }

        foreach (var metadata in indexes)
        {
            foreach (string name in metadata.ColumnNames)
            {
                var column = table.Columns[FindColumnOrdinal(table, name)];
                if (column.Type.Type is Types.DatabaseType.String or Types.DatabaseType.Json
                    && !(column.Collation ?? _catalog.DefaultCollation).IsIndexBacked)
                {
                    throw new DatabaseException($"Collation 'invariant' is not index-backed; column '{name}' cannot have an index or indexed constraint.");
                }
            }
        }

        var created = new List<string>();
        try
        {
            if (indexes.Count > 0)
            {
                // Durable, but not a self-commit the session counts (#1272): the trees are orphaned
                // pages until the publish below describes them.
                await statement.Coordinator.ApplyStatementAsync<bool>(statement.Transaction, async bracket =>
                {
                    foreach (var metadata in indexes)
                    {
                        var index = await _indexManager.CreateIndexAsync(statement.Transaction, table.ObjectId,
                            new IndexDefinition(metadata.Name, IndexKind.BTree, true), cancellationToken).ConfigureAwait(false);
                        created.Add(metadata.Name);
                        var ordinals = metadata.ColumnNames.Select(column => FindColumnOrdinal(table, column)).ToArray();
                        var keys = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var version in ScanVersions(table, cancellationToken))
                        {
                            var key = BuildIndexKey(table, ordinals, version.Values);
                            if (version.Deleter == TransactionSequence.None && !keys.Add(Convert.ToHexString(key.Encoded.Span)))
                            {
                                throw new SqlConstraintViolationException(metadata.Name, $"{table.Schema}.{table.Name}", "UNIQUE");
                            }

                            await index.InsertVersionAsync(bracket, key, SqlRecordLocation.Pack(version.Location.PageId, version.Location.SlotIndex),
                                version.Writer, version.Deleter, cancellationToken).ConfigureAwait(false);
                        }
                    }
                    return true;
                }, durable: true, cancellationToken).ConfigureAwait(false);
            }
            await SelfCommitAsync(statement, _catalog.PublishTableAsync(table, indexes, _indexManager.ExportRegistrations(),
                replaceExisting, cancellationToken)).ConfigureAwait(false);
        }
        catch
        {
            foreach (string index in created)
            {
                await _indexManager.DropIndexAsync(statement.Transaction, table.ObjectId, index, CancellationToken.None).ConfigureAwait(false);
            }

            throw;
        }
    }

    private async Task<QueryResult> ExecuteAddConstraintAsync(SqlAddConstraintPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        // The backfill below validates every existing row against latest state, so
        // it needs table-grain exclusivity, not the row-grain referential locks
        // DML uses: the Exclusive lock on this table taken here, plus the
        // Exclusive locks on every referenced parent taken by
        // LockReferencedTablesAsync once the constraint has been bound.
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE ADD CONSTRAINT", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        if (plan.Constraint.Kind == SqlConstraintKind.PrimaryKey && plan.Table.PrimaryKeyColumns.Count > 0)
        {
            throw new DatabaseException($"Table '{plan.Table.Name}' already has a primary key.");
        }

        var constraints = BindConstraints(plan.Table, [plan.Constraint]);
        await LockReferencedTablesAsync(constraints, statement, cancellationToken).ConfigureAwait(false);
        constraints = BindConstraints(plan.Table, [plan.Constraint]);
        var primary = plan.Constraint.Kind == SqlConstraintKind.PrimaryKey ? plan.Constraint.Columns : plan.Table.PrimaryKeyColumns;
        var columns = plan.Table.Columns.Select(column => primary.Contains(column.Name, StringComparer.OrdinalIgnoreCase)
            ? new SqlCatalogColumn(column.Name, column.Type, false, column.DefaultLiteral, column.Collation) : column).ToArray();
        var replacement = new SqlCatalogTable(plan.Table.ObjectId, plan.Table.Schema, plan.Table.Name, columns,
            primary, plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints.Concat(constraints).ToArray(),
            plan.Table.DroppedColumnOrdinals);
        var rows = Scan(plan.Table, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Select(row => row.Values).ToList();
        foreach (var row in rows)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                CoerceForColumn(row[i], columns[i]);
            }
        }

        // The backfill binds the replacement version, which is the instance published below.
        ValidateRows(replacement, rows, statement, cancellationToken, current: true);
        await PublishConstrainedTableAsync(replacement, [plan.Constraint], statement, cancellationToken, replaceExisting: true).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private async Task<QueryResult> ExecuteAddColumnAsync(SqlAddColumnPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Schema, plan.Name, "ALTER TABLE ADD COLUMN", cancellationToken).ConfigureAwait(false);
        _catalog.TryGetTable(plan.Schema, plan.Name, out var table);
        if (table.FindColumn(plan.Column.Name) is not null)
        {
            throw new DatabaseException($"Column '{plan.Column.Name}' already exists.");
        }

        // Validate even on an empty table: publishing an unusable default would
        // defer the failure until a later INSERT. No catalog or row writes occur
        // until every default and existing-row constraint has been checked.
        if (plan.Column.DefaultLiteral is not null)
        {
            ResolveDefault(plan.Column, SqlPersistedExpression.LoadDefaultValue(plan.Column.DefaultLiteral,
                $"DEFAULT of column '{plan.Column.Name}' on table '{plan.Schema}.{plan.Name}'"));
        }

        var columns = table.Columns.Append(plan.Column).ToArray();
        var primaryDefinition = plan.Constraints.FirstOrDefault(c => c.Kind == SqlConstraintKind.PrimaryKey);
        if (primaryDefinition is not null && table.PrimaryKeyColumns.Count > 0)
        {
            throw new DatabaseException("The table already has a primary key.");
        }

        var primary = primaryDefinition?.Columns ?? table.PrimaryKeyColumns;
        // The added column takes the next physical ordinal: the replacement keeps the
        // table's dropped ordinals, so every existing column keeps its own (the catalog
        // refuses a replacement that does not).
        var provisional = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, primary, table.Owner, table.OwningSchema,
            table.Constraints, table.DroppedColumnOrdinals);
        var constraints = BindConstraints(provisional, plan.Constraints);
        await LockReferencedTablesAsync(constraints, statement, cancellationToken).ConfigureAwait(false);
        constraints = BindConstraints(provisional, plan.Constraints);
        var replacement = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, primary, table.Owner, table.OwningSchema,
            table.Constraints.Concat(constraints).ToArray(), table.DroppedColumnOrdinals);
        // Backfill is logical: missing trailing fields resolve from the immutable
        // replacement metadata. Existing row bytes and MVCC stamps never change;
        // one durable catalog publication makes the complete addition visible.
        var rows = Scan(replacement, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Select(row => row.Values).ToList();
        foreach (var row in rows)
        {
            CoerceForColumn(row[^1], plan.Column);
        }

        ValidateRows(replacement, rows, statement, cancellationToken, current: true);
        if (plan.Constraints.Count == 0)
        {
            // The catalog publishes its own copy of the replacement definition, built from the
            // same column and constraint instances, so it adopts the replacement's bindings.
            _definitions.Adopt(await SelfCommitAsync(statement, _catalog.AddColumnAsync(plan.Schema, plan.Name, plan.Column, cancellationToken)).ConfigureAwait(false),
                replacement);
        }
        else
        {
            await PublishConstrainedTableAsync(replacement, plan.Constraints, statement, cancellationToken, replaceExisting: true).ConfigureAwait(false);
        }
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private async Task<QueryResult> ExecuteDropConstraintAsync(SqlDropConstraintPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE DROP CONSTRAINT", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        if (plan.Table.Constraints.Any(c => string.Equals(c.Name, plan.ConstraintName, StringComparison.OrdinalIgnoreCase)))
        {
            // The new version no longer carries the constraint, so the dropped predicate is
            // never evaluated again; it keeps the other bindings of the version it came from.
            _definitions.Adopt(await SelfCommitAsync(statement, _catalog.DropConstraintAsync(plan.Table.Schema, plan.Table.Name, plan.ConstraintName,
                cancellationToken)).ConfigureAwait(false), plan.Table);
        }
        else if (_catalog.TryGetIndex(plan.Table.ObjectId, plan.ConstraintName, out var index) && index.IsUnique)
        {
            return await ExecuteDropIndexAsync(new SqlDropIndexPlan(plan.Table, plan.ConstraintName, false), statement, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new DatabaseException($"Constraint '{plan.ConstraintName}' does not exist on '{plan.Table.Name}'.");
        }

        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private void EnsureCanDropIndex(SqlCatalogTable table, SqlCatalogIndex index)
    {
        if (index.IsUnique && (index.IsPrimaryKey ||
            IncomingReferences(table).Any(reference => SameConstraintColumns(reference.Constraint.ReferencedColumns!, index.ColumnNames))))
        {
            throw new DatabaseException($"Index '{index.Name}' supports a primary key or foreign key and cannot be dropped.");
        }
    }

    /// <summary>
    /// Refuses to drop a column a constraint uses, naming the constraint to drop first, with
    /// the catalog's wording. A missing column and a primary-key column are left to the
    /// catalog's own refusals, which name them as such.
    /// </summary>
    private void EnsureCanDropColumn(SqlCatalogTable table, string columnName)
    {
        int ordinal = -1;
        for (int index = 0; index < table.Columns.Count; index++)
        {
            if (string.Equals(table.Columns[index].Name, columnName, StringComparison.OrdinalIgnoreCase))
            {
                ordinal = index;
                break;
            }
        }

        if (ordinal < 0 || table.PrimaryKeyColumns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var constraint in table.Constraints)
        {
            if (constraint.Columns.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                throw UsedByConstraint(constraint);
            }
        }

        foreach (var reference in IncomingReferences(table))
        {
            if (reference.Constraint.ReferencedColumns!.Contains(columnName, StringComparer.OrdinalIgnoreCase))
            {
                throw UsedByConstraint(reference.Constraint);
            }
        }

        // A table-level CHECK lists no columns; its bound predicate records every column it
        // reads. Binding the predicate without the column (binding only, as at load, so a
        // declaration rule tightened since the check was stored cannot block an unrelated
        // drop) backs that up, and either way the refusal names the CHECK, not the binder's
        // unknown column.
        var columns = table.Columns.Where((_, index) => index != ordinal).ToArray();
        foreach (var check in _definitions.Get(table).Checks)
        {
            if (check.ColumnOrdinals.Contains(ordinal))
            {
                throw UsedByConstraint(check.Constraint);
            }

            try
            {
                SqlPersistedExpression.Bind(check.Predicate, new SqlExpressionEvaluator(columns, null, defaultCollation: _catalog.DefaultCollation));
            }
            catch (DatabaseException exception)
            {
                throw UsedByConstraint(check.Constraint, exception);
            }
        }

        DatabaseException UsedByConstraint(SqlCatalogConstraint constraint, Exception? cause = null)
            => new($"Column '{columnName}' is referenced by constraint '{constraint.Name}'. Drop the constraint first.", cause);
    }
}
