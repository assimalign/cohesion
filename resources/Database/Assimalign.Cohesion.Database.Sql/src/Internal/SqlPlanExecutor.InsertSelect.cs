using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    /// <summary>
    /// Materializes the entire source at the outer statement's MVCC snapshot
    /// before writing. Reading the destination is supported without feeding newly
    /// inserted rows back into the query.
    /// </summary>
    private async Task<QueryResult> ExecuteInsertSelectAsync(SqlInsertSelectPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        await using var source = (QueryResultSet)await ExecuteAsync(plan.Source, statement, cancellationToken).ConfigureAwait(false);
        if (source.Columns.Count != plan.TargetOrdinals.Count)
        {
            throw new DatabaseException(
                $"INSERT SELECT has {source.Columns.Count} source columns but {plan.TargetOrdinals.Count} target columns.");
        }
        var rows = new List<object?[]>();
        await foreach (var sourceRow in source.GetRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = new object?[sourceRow.FieldCount];
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = sourceRow.GetValue(i);
            }
            rows.Add(row);
        }
        return await ExecuteInsertRowsAsync(plan.Table, plan.TargetOrdinals, rows, statement, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The shared literal/query insertion path: coerce values, apply omitted
    /// defaults, validate all row constraints, acquire reference and unique locks,
    /// then publish the complete write set within one transactional statement.
    /// </summary>
    private async Task<QueryResult> ExecuteInsertRowsAsync(SqlCatalogTable table, IReadOnlyList<int> targetOrdinals,
        IReadOnlyList<object?[]> sourceRows, SqlStatementContext statement, CancellationToken cancellationToken)
    {
        await AcquireOutgoingReferenceIntentLocksAsync(table, statement, cancellationToken).ConfigureAwait(false);
        await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence, LockResource.Object(table.ObjectId),
            LockMode.IntentExclusive, cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(table);
        var indexes = GetLiveIndexes(table);
        var rows = new List<(byte[] Record, object?[] Values)>(sourceRows.Count);
        foreach (var sourceRow in sourceRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceRow.Length != targetOrdinals.Count)
            {
                throw new DatabaseException(
                    $"INSERT row has {sourceRow.Length} values but {targetOrdinals.Count} target columns.");
            }
            var values = new object?[table.Columns.Count];
            var assigned = new bool[table.Columns.Count];
            for (int i = 0; i < targetOrdinals.Count; i++)
            {
                int ordinal = targetOrdinals[i];
                values[ordinal] = CoerceForColumn(sourceRow[i], table.Columns[ordinal]);
                assigned[ordinal] = true;
            }
            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                if (!assigned[ordinal])
                {
                    values[ordinal] = ResolveDefault(table.Columns[ordinal]);
                }
            }
            rows.Add((SqlRowCodec.Encode(table.ObjectId, table.Columns, values, statement.Transaction.Sequence), values));
        }

        // As with literal INSERT, acquire reference locks and unique-key locks
        // before the apply gate. Every row is validated before the first write.
        var references = new List<SqlParentReference>();
        ValidateRows(table, rows.Select(row => row.Values).ToList(), statement, cancellationToken, references: references);
        await AcquireParentRowLocksAsync(references, statement, cancellationToken).ConfigureAwait(false);
        var uniqueKeyHashes = new List<ulong>();
        foreach (var (_, values) in rows)
        {
            CollectUniqueKeyHashes(indexes, table, values, uniqueKeyHashes);
        }
        await AcquireUniqueKeyLocksAsync(statement, table.ObjectId, uniqueKeyHashes, cancellationToken).ConfigureAwait(false);
        try
        {
            return await statement.Coordinator.ApplyStatementAsync(statement.Transaction, async bracket =>
            {
                foreach (var (record, values) in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (pageId, slotIndex) = _storage.InsertRow(bracket, table.ObjectId, record);
                    statement.Coordinator.VersionStore.RecordCreated(statement.Transaction.Sequence, pageId, slotIndex);
                    await InsertIndexEntriesAsync(statement, indexes, table, values,
                        SqlRecordLocation.Pack(pageId, slotIndex), cancellationToken).ConfigureAwait(false);
                }
                return (QueryResult)new SqlQueryResult(QueryResultStatus.Success, rows.Count);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            throw TranslateUniqueViolation(table, exception);
        }
    }
}
