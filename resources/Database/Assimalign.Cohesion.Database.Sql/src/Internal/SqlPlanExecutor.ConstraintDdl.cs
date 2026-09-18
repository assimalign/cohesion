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
        var provisional = new SqlCatalogTable(0, plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey);
        var constraints = BindConstraints(provisional, plan.Constraints);
        await LockReferencedTablesAsync(constraints, statement, cancellationToken).ConfigureAwait(false);
        // Rebind after waiting: a parent definition might have changed while acquiring its lock.
        constraints = BindConstraints(provisional, plan.Constraints);
        var table = await SqlCatalog.ReserveTableAsync(_catalog, plan.Schema, plan.Name, plan.Columns, plan.PrimaryKey, constraints,
            statement.ProvisioningSchema is null ? DatabaseObjectOwner.Adhoc : DatabaseObjectOwner.Schema,
            statement.ProvisioningSchema, cancellationToken).ConfigureAwait(false);
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
            await SqlCatalog.PublishTableAsync(_catalog, table, indexes, ((IIndexRegistry)_indexManager).ExportRegistrations(),
                cancellationToken, replaceExisting).ConfigureAwait(false);
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
            primary, plan.Table.Owner, plan.Table.OwningSchema, plan.Table.Constraints.Concat(constraints).ToArray());
        var rows = Scan(plan.Table, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Select(row => row.Values).ToList();
        foreach (var row in rows)
        {
            for (int i = 0; i < columns.Length; i++)
            {
                CoerceForColumn(row[i], columns[i]);
            }
        }

        ValidateRows(replacement, rows, statement, cancellationToken, current: true);
        await PublishConstrainedTableAsync(replacement, [plan.Constraint], statement, cancellationToken, replaceExisting: true).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private async Task<QueryResult> ExecuteAddColumnAsync(SqlAddColumnPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Schema, plan.Name, "ALTER TABLE ADD COLUMN", cancellationToken).ConfigureAwait(false);
        _catalog.TryGetTable(plan.Schema, plan.Name, out var table);
        if (plan.Constraints.Count == 0)
        {
            await _catalog.AddColumnAsync(plan.Schema, plan.Name, plan.Column, cancellationToken).ConfigureAwait(false);
            return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
        }
        if (table.FindColumn(plan.Column.Name) is not null)
        {
            throw new DatabaseException($"Column '{plan.Column.Name}' already exists.");
        }

        var columns = table.Columns.Append(plan.Column).ToArray();
        var primaryDefinition = plan.Constraints.FirstOrDefault(c => c.Kind == SqlConstraintKind.PrimaryKey);
        if (primaryDefinition is not null && table.PrimaryKeyColumns.Count > 0)
        {
            throw new DatabaseException("The table already has a primary key.");
        }

        var primary = primaryDefinition?.Columns ?? table.PrimaryKeyColumns;
        var provisional = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, primary, table.Owner, table.OwningSchema, table.Constraints);
        var constraints = BindConstraints(provisional, plan.Constraints);
        await LockReferencedTablesAsync(constraints, statement, cancellationToken).ConfigureAwait(false);
        var replacement = new SqlCatalogTable(table.ObjectId, table.Schema, table.Name, columns, primary, table.Owner, table.OwningSchema,
            table.Constraints.Concat(constraints).ToArray());
        // Existing ADD COLUMN semantics decode missing trailing fields as NULL.
        var rows = Scan(replacement, statement, cancellationToken, ConstraintCurrentSnapshot(statement)).Select(row => row.Values).ToList();
        foreach (var row in rows)
        {
            CoerceForColumn(row[^1], plan.Column);
        }

        ValidateRows(replacement, rows, statement, cancellationToken, current: true);
        await PublishConstrainedTableAsync(replacement, plan.Constraints, statement, cancellationToken, replaceExisting: true).ConfigureAwait(false);
        return new SqlQueryResult(QueryResultStatus.Success, affectedCount: 0);
    }

    private async Task<QueryResult> ExecuteDropConstraintAsync(SqlDropConstraintPlan plan, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireObjectLockAsync(statement, plan.Table.Schema, plan.Table.Name, "ALTER TABLE DROP CONSTRAINT", cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(plan.Table);
        if (plan.Table.Constraints.Any(c => string.Equals(c.Name, plan.ConstraintName, StringComparison.OrdinalIgnoreCase)))
        {
            await SqlCatalog.DropConstraintAsync(_catalog, plan.Table.Schema, plan.Table.Name, plan.ConstraintName, cancellationToken).ConfigureAwait(false);
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

    private void EnsureCanDropColumn(SqlCatalogTable table, string columnName)
    {
        if (table.Constraints.Any(c => c.Columns.Contains(columnName, StringComparer.OrdinalIgnoreCase)) ||
            IncomingReferences(table).Any(reference => reference.Constraint.ReferencedColumns!.Contains(columnName, StringComparer.OrdinalIgnoreCase)))
        {
            throw new DatabaseException($"Column '{columnName}' participates in a constraint and cannot be dropped.");
        }

        var columns = table.Columns.Where(column => !string.Equals(column.Name, columnName, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var check in table.Constraints.Where(c => c.Kind == SqlCatalogConstraintKind.Check))
        {
            SqlPlanner.ValidateExpression(ParseCheck(check.CheckExpression!), new SqlExpressionEvaluator(columns, null, defaultCollation: _catalog.DefaultCollation));
        }
    }
}
