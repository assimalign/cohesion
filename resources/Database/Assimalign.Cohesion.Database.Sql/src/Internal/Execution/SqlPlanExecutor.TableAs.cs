using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    internal static Action<string, ulong>? TableAsStage { get; set; }
    internal static Action<long>? TableAsSourceAdmitted { get; set; }
    internal static Action<SqlSpilledResultSet, DatabaseRow>? TableAsSourceReading { get; set; }
    internal static Action<byte[]>? TableAsRecordEncoded { get; set; }

    private async Task<DatabaseResult> ExecuteCreateTableAsAsync(SqlCreateTableAsPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken, SqlDatabaseSession? spillOwner = null)
    {
        SqlNamespaces.Require(_catalog, plan.Schema);
        if (plan.IfNotExists && _catalog.TryGetTable(plan.Schema, plan.Name, out _))
        {
            if (statement.ProvisioningSchema is not null)
            {
                throw new SqlExecutionException(SqlExecutionException.DuplicateTableCode,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableProvisioningCollision, plan.Schema, plan.Name));
            }
            return await CompleteAsync(0).ConfigureAwait(false);
        }
        if (plan.Source is null) { throw new SqlCatalogException("CS04064", ErrorMessages.TableAsTargetChanged); }

        // Lock/latch order: session DDL admission -> metadata lease -> reserved owner X
        // -> coordinator apply gate -> data page latches. Catalog monitor/brackets are
        // innermost and never overlap the apply gate or a data latch. Source SELECT uses
        // the statement's pinned MVCC snapshot; joins retain source IS locks. The epoch
        // spans reservation, source disposal and every failure/rollback path, so a worker
        // discovering the abandonment intent cannot reclaim pages while we build them.
        var table = await _catalog.ReserveTableAsAsync(plan.Schema, plan.Name, plan.Columns,
            statement.ProvisioningSchema, statement.ScriptReceipt, plan.IfNotExists && statement.ProvisioningSchema is null,
            cancellationToken).ConfigureAwait(false);
        if (table is null) { return await CompleteAsync(0).ConfigureAwait(false); }
        long written = 0;
        TransactionContext? builder = null;
        try
        {
            TableAsStage?.Invoke("Reserved", table.ObjectId);
            await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
                LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
            var bound = _definitions.Get(table, cancellationToken);
            if (plan.WithData)
            {
                builder = await statement.Coordinator.BeginAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
                // The source borrows the root's owner only until this statement disposes it.
                // A fitting source retains its exact admission; a spilled source streams into
                // 64-row brackets. Temp reads finish outside every apply bracket/page latch.
                long sourceBytes = 0, batchBytes = 0, bufferBytes = 0;
                try
                {
                    await using var source = (DatabaseResultSet)await ExecuteRootAsync(
                        Cost(plan.Source, statement, cancellationToken), statement, cancellationToken, spillOwner).ConfigureAwait(false);
                    try
                    {
                        if (source is SqlMaterializedResultSet materialized)
                        { sourceBytes = materialized.ReserveMemory(statement.Metrics, _definitions.StatementMemoryBudget, cancellationToken); }
                        TableAsSourceAdmitted?.Invoke(sourceBytes);
                        statement.Metrics.ReserveHashMemory(24L + 64L * 8, _definitions.StatementMemoryBudget);
                        bufferBytes = 24L + 64L * 8;
                        var batch = new List<byte[]>(64);
                        var domains = bound.Domains.Count == 0 ? null : ExecutionEvaluator(null, cancellationToken,
                            transactionTimestamp: statement.TransactionStart);
                        await foreach (var row in source.GetRowsAsync(cancellationToken).ConfigureAwait(false))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (source is SqlSpilledResultSet spilled) { TableAsSourceReading?.Invoke(spilled, row); }
                            // Bound each physical bracket and release the apply gate between batches.
                            if (batch.Count == 64) { await FlushAsync().ConfigureAwait(false); }
                            var values = new object?[table.Columns.Count];
                            for (int ordinal = 0; ordinal < values.Length; ordinal++)
                            {
                                values[ordinal] = CoerceForColumn(SqlCharacter.Input(row.GetValue(ordinal), source.Columns[ordinal].Type),
                                    table.Columns[ordinal]);
                            }
                            if (domains is not null) { CheckDomains(bound, values, domains); }
                            byte[] record = SqlRowCodec.Encode(table, values, builder.Sequence);
                            TableAsRecordEncoded?.Invoke(record);
                            long bytes = 32L + record.Length;
                            statement.Metrics.ReserveHashMemory(bytes, _definitions.StatementMemoryBudget);
                            batchBytes += bytes;
                            batch.Add(record);
                            written++;
                        }
                        if (batch.Count != 0) { await FlushAsync().ConfigureAwait(false); }
                        DisposeCompletedTableAsSource(source);

                        async Task FlushAsync()
                        {
                            // The ordered journal makes every bracket durable with the builder's
                            // later commit, whose fsync runs outside the apply gate.
                            await statement.Coordinator.ApplyStatementAsync(builder, bracket =>
                            {
                                foreach (var record in batch)
                                {
                                    cancellationToken.ThrowIfCancellationRequested();
                                    // The abandonment intent undoes the entire unreachable owner;
                                    // no per-row undo ledger needs to survive each bounded bracket.
                                    _storage.InsertRow(bracket, table.ObjectId, record);
                                }
                                return ValueTask.FromResult(true);
                            }, false, cancellationToken).ConfigureAwait(false);
                            batch.Clear();
                            statement.Metrics.ReleaseHashMemory(batchBytes);
                            batchBytes = 0;
                            TableAsStage?.Invoke("Copied", table.ObjectId);
                        }
                    }
                    catch (Exception failure)
                    {
                        // Preserve the source/copy failure if temporary cleanup also fails.
                        if (source is SqlSpilledResultSet spilled) { spilled.DisposeAfterFailure(failure); }
                        throw;
                    }
                }
                finally { statement.Metrics.ReleaseHashMemory(sourceBytes + batchBytes + bufferBytes); }
            }
            TableAsStage?.Invoke("Prepared", table.ObjectId);
            var publication = statement.ScriptReceipt;
            if (publication is { } expected)
            {
                var previous = expected.Previous!;
                ulong generation = checked(expected.ExpectedGeneration + 1);
                var reserved = new SqlCatalogScript(previous.Name, previous.ContentHash, previous.StatementCount,
                    previous.CompletedStatements, previous.BaseGeneration, generation, previous.SchemaHash,
                    previous.Objects, previous.StatementObjects, previous.StatementParameterTypes);
                publication = new SqlCatalogScriptPublication(expected.Name, expected.ContentHash, expected.StatementCount,
                    reserved, generation, expected.SchemaHash, expected.ExpectedObject);
            }
            // Prepare the actual completion before either irrevocable publication or an
            // observer of it. A duplicate-name no-op can still prepare its zero completion.
            var result = await CompleteAsync(written).ConfigureAwait(false);
            // One builder commit controls row visibility. Snapshots taken before it see
            // no rows; those taken afterwards see all rows once publication permits binding.
            if (builder is not null)
            {
                await statement.Coordinator.CommitAsync(builder, cancellationToken).ConfigureAwait(false);
                builder = null;
            }
            TableAsStage?.Invoke("BuilderCommitted", table.ObjectId);
            if (!await _catalog.PublishTableAsAsync(table, plan.IfNotExists && statement.ProvisioningSchema is null,
                publication, cancellationToken).ConfigureAwait(false))
            {
                return await CompleteAsync(0).ConfigureAwait(false);
            }
            statement.Metrics.RecordSelfCommit();
            AfterSelfCommit?.Invoke();
            return result;
        }
        finally
        {
            try
            {
                if (builder is { State: TransactionState.Active } && !_storage.IsOffline)
                { await statement.Coordinator.RollbackAsync(builder, CancellationToken.None).ConfigureAwait(false); }
            }
            finally { _catalog.AbandonTableAs(table.ObjectId); }
        }

        ValueTask<DatabaseResult> CompleteAsync(long count)
            => statement.PrepareResultAsync(new SqlQueryResult(DatabaseResultStatus.Success, count), cancellationToken);
    }

    private static void DisposeCompletedTableAsSource(DatabaseResultSet source)
    {
        if (source is not SqlSpilledResultSet completed) { return; }
        // As with completed wire delivery, temporary cleanup cannot replace a completed
        // copy. Storage counts the translated close failure. Keep this synchronous so
        // fitting CTAS retains its existing async frame and allocation shape.
        try { completed.Dispose(); }
        catch (SqlExecutionException) { }
    }
}
