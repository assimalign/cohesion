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

    private async Task<DatabaseResult> ExecuteCreateTableAsAsync(SqlCreateTableAsPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        SqlNamespaces.Require(_catalog, plan.Schema);
        if (plan.IfNotExists && _catalog.TryGetTable(plan.Schema, plan.Name, out _))
        {
            if (statement.ProvisioningSchema is not null)
            {
                throw new SqlExecutionException(SqlExecutionException.DuplicateTableCode,
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.TableProvisioningCollision, plan.Schema, plan.Name));
            }
            return new SqlQueryResult(DatabaseResultStatus.Success, 0);
        }
        if (plan.Source is null) { throw new SqlCatalogException("CS04064", ErrorMessages.TableAsTargetChanged); }

        // Lock/latch order: session DDL admission -> metadata lease -> reserved owner X
        // -> coordinator apply gate -> data page latches. Catalog monitor/brackets are
        // innermost and never overlap the apply gate or a data latch. Source SELECT uses
        // the statement's pinned MVCC snapshot without source table locks. The epoch
        // spans reservation, source disposal and every failure/rollback path, so a worker
        // discovering the abandonment intent cannot reclaim pages while we build them.
        var table = await _catalog.ReserveTableAsAsync(plan.Schema, plan.Name, plan.Columns,
            statement.ProvisioningSchema, statement.ScriptReceipt, cancellationToken).ConfigureAwait(false);
        TableAsStage?.Invoke("Reserved", table.ObjectId);
        await statement.Coordinator.LockManager.AcquireAsync(statement.DdlLockOwner,
            LockResource.Object(table.ObjectId), LockMode.Exclusive, cancellationToken).ConfigureAwait(false);
        var bound = _definitions.Get(table, cancellationToken);
        if (plan.WithData)
        {
            // Query operators currently materialize their output, as INSERT SELECT does.
            // Storage brackets are bounded to 64 rows and release the apply gate between
            // batches. Nothing is reachable through the catalog until all batches commit.
            await using var source = (DatabaseResultSet)await ExecuteAsync(
                Cost(plan.Source, statement, cancellationToken), statement, cancellationToken).ConfigureAwait(false);
            var batch = new List<byte[]>(64);
            var domains = bound.Domains.Count == 0 ? null : ExecutionEvaluator(null, cancellationToken,
                transactionTimestamp: statement.TransactionStart);
            await foreach (var row in source.GetRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = new object?[table.Columns.Count];
                for (int ordinal = 0; ordinal < values.Length; ordinal++)
                {
                    values[ordinal] = CoerceForColumn(SqlCharacter.Input(row.GetValue(ordinal), source.Columns[ordinal].Type),
                        table.Columns[ordinal]);
                }
                if (domains is not null) { CheckDomains(bound, values, domains); }
                // These frozen rows have no transactional writer. Their durable storage
                // precedes the single catalog commit; publication makes all rows visible
                // together, including to a reader admitted before the CTAS session ends.
                batch.Add(SqlRowCodec.Encode(table, values, TransactionSequence.None));
                if (batch.Count == 64) { await FlushAsync().ConfigureAwait(false); }
            }
            if (batch.Count != 0) { await FlushAsync().ConfigureAwait(false); }

            async Task FlushAsync()
            {
                await statement.Coordinator.ApplyStatementAsync(statement.Transaction, bracket =>
                {
                    foreach (var record in batch)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        _storage.InsertRow(bracket, table.ObjectId, record);
                    }
                    return ValueTask.FromResult(true);
                }, durable: true, cancellationToken).ConfigureAwait(false);
                batch.Clear();
                TableAsStage?.Invoke("Copied", table.ObjectId);
            }
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
        // No caller cancellation or fallible source disposal after this irrevocable commit.
        await SelfCommitAsync(statement, publication is null
            ? _catalog.PublishTableAsync(table, [], [], cancellationToken: cancellationToken)
            : _catalog.PublishTableAsync(table, [], [], false, publication, cancellationToken)).ConfigureAwait(false);
        return new SqlQueryResult(DatabaseResultStatus.Success, 0);
    }
}
