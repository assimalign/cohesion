using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action? TruncateReserved { get; set; }
    internal static Action? TruncatePrepared { get; set; }

    private async Task<DatabaseResult> ExecuteTruncateAsync(SqlTruncatePlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        var image = _catalog.CaptureSnapshot();
        var targets = new Dictionary<ulong, SqlCatalogTable>();
        foreach (var target in plan.Expression.Tables)
        {
            var reference = target.Table;
            string schema = reference.SchemaName ?? SqlPlanner.DefaultSchema;
            SqlNamespaces.Require(_catalog, schema);
            if (SqlSystemViews.Find(reference) is { } system)
            {
                throw new SqlExecutionException("CS04021",
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.ExecutorExecuteRenameAsyncSqlPlanning, system.Schema, system.Name));
            }
            if (image.Views.FirstOrDefault(view => view.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                view.Name.Equals(reference.TableName, StringComparison.OrdinalIgnoreCase)) is { } view)
            {
                throw new SqlExecutionException("CS04021", string.Format(CultureInfo.CurrentCulture,
                    ErrorMessages.ViewReadOnly, view.Schema, view.Name));
            }
            if (!image.TryGetTable(schema, reference.TableName, out var table))
            {
                throw new SqlExecutionException(SqlExecutionException.UndefinedTableCode,
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.TableMissing, schema, reference.TableName));
            }
            // PostgreSQL tablecmds.c:1915 ignores duplicate relations in the written list.
            targets.TryAdd(table.ObjectId, table);
        }
        bool added;
        do
        {
            added = false;
            foreach (var child in image.Tables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (targets.ContainsKey(child.ObjectId)) { continue; }
                if (!child.Constraints.Any(constraint => constraint.Kind == SqlCatalogConstraintKind.Reference &&
                    targets.Values.Any(parent => string.Equals(parent.Schema, constraint.ReferencedSchema, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(parent.Name, constraint.ReferencedTable, StringComparison.OrdinalIgnoreCase)))) { continue; }
                if (!plan.Expression.Cascade)
                {
                    throw new SqlExecutionException("CS03067", string.Format(CultureInfo.CurrentCulture,
                        ErrorMessages.TruncateForeignKeyDependent, child.Schema, child.Name));
                }
                targets.Add(child.ObjectId, child);
                added = true;
            }
        } while (added);

        // Lock/latch order: metadata admission precedes ascending table X locks (old and
        // reserved owners), then coordinator apply gate, tree directory/tree latch/page pins.
        // No apply gate or page/tree latch spans a catalog publication. The catalog lock's
        // own storage bracket is innermost. Reclamation waits for transaction AND statement
        // pins, so cancellation/rollback cannot expose pages while this flow still uses them.
        var previous = targets.Values.OrderBy(table => table.ObjectId).ToArray();
        for (int index = 0; index < previous.Length; index++)
        {
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(previous[index].ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            var current = ReadCurrentTable(previous[index]);
            EnsureSameIdentity(previous[index], current);
            previous[index] = current;
        }
        var oldIndexes = previous.SelectMany(table => _catalog.GetIndexes(table.ObjectId)).ToArray();
        var replacements = _catalog.ReserveTruncate(previous, cancellationToken);
        var mapping = previous.Select((table, index) => (table.ObjectId, Replacement: replacements[index]))
            .ToDictionary(pair => pair.ObjectId, pair => pair.Replacement);
        var indexes = oldIndexes.Select(index => new SqlCatalogIndex(mapping[index.TableObjectId].ObjectId,
            index.Name, index.ColumnNames, index.IsUnique, index.Owner, index.OwningSchema, index.IsPrimaryKey)).ToArray();
        var created = new List<SqlCatalogIndex>();
        bool published = false;
        Exception? operationFailure = null;
        try
        {
            TruncateReserved?.Invoke();
            foreach (var table in replacements)
            {
                await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                    LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
                _definitions.Get(table, cancellationToken);
            }
            if (indexes.Length != 0)
            {
                await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async _ =>
                {
                    foreach (var index in indexes)
                    {
                        await _indexManager.CreateIndexAsync(statement.Transaction, index.TableObjectId,
                            new IndexDefinition(index.Name, IndexKind.BTree, index.IsUnique), cancellationToken).ConfigureAwait(false);
                        created.Add(index);
                    }
                    return true;
                }, durable: true, cancellationToken).ConfigureAwait(false);
            }
            TruncatePrepared?.Invoke();
            await _catalog.TruncateSnapshotGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            TransactionContext? marker = null;
            try
            {
                marker = await statement.Coordinator.BeginSnapshotPinAsync(cancellationToken).ConfigureAwait(false);
                await _catalog.PublishTruncateAsync(previous, replacements, indexes, PublishedRegistrations(indexes),
                    plan.Expression.RestartIdentity, marker.Sequence,
                    SqlObjectIdentity.Table(plan.Expression.Tables[0].Table.SchemaName ?? SqlPlanner.DefaultSchema, plan.Expression.Tables[0].Table.TableName),
                    statement.ScriptReceipt, cancellationToken).ConfigureAwait(false);
                // Set before any observer can throw: publication is irrevocable, and cleanup
                // must remove old handles even when cancellation or a test observer intervenes.
                published = true;
                statement.Metrics.RecordSelfCommit();
            }
            finally
            {
                try
                {
                    if (marker is not null)
                    {
                        await statement.Coordinator.RollbackAsync(marker, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                finally { _catalog.TruncateSnapshotGate.Release(); }
            }
            AfterSelfCommit?.Invoke();
        }
        catch (Exception failure)
        {
            // Preserve the original coded storage/cancellation failure if mandatory
            // directory cleanup also fails; neither outcome may hide the other.
            operationFailure = failure;
            throw;
        }
        finally
        {
            try
            {
                await RemoveDroppedIndexesAsync(statement, published ? oldIndexes : created,
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception cleanupFailure) when (operationFailure is not null)
            {
                throw new AggregateException(operationFailure, cleanupFailure);
            }
        }
        return new SqlQueryResult(DatabaseResultStatus.Success, affectedCount: 0);
    }
}
