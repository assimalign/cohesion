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
    private async Task<DatabaseResult> ExecuteInsertSelectAsync(SqlInsertSelectPlan plan, SqlStatementContext statement,
        CancellationToken cancellationToken)
    {
        await using var source = (DatabaseResultSet)await ExecuteAsync(plan.Source, statement, cancellationToken).ConfigureAwait(false);
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
                row[i] = SqlCharacter.Input(sourceRow.GetValue(i), source.Columns[i].Type);
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
    private async Task<DatabaseResult> ExecuteInsertRowsAsync(SqlCatalogTable table, IReadOnlyList<int> targetOrdinals,
        IReadOnlyList<object?[]> sourceRows, SqlStatementContext statement, CancellationToken cancellationToken,
        IReadOnlyList<SqlBoundExpression[]>? valueRows = null)
    {
        await AcquireOutgoingReferenceIntentLocksAsync(table, statement, cancellationToken).ConfigureAwait(false);
        await statement.Coordinator.LockManager.AcquireAsync(statement.Transaction.Sequence, LockResource.Object(table.ObjectId),
            LockMode.IntentExclusive, cancellationToken).ConfigureAwait(false);
        EnsureCurrentDefinition(table);
        var indexes = GetLiveIndexes(table);
        var bound = _definitions.Get(table, cancellationToken);
        var defaults = bound.DefaultValues;
        var domainEvaluator = bound.Domains.Count == 0 ? null : ExecutionEvaluator(null, cancellationToken);
        var rows = new List<(byte[] Record, object?[] Values)>(sourceRows.Count);
        int identityOrdinal = -1;
        int identityTargetIndex = -1;
        for (int ordinal = 0; ordinal < table.Columns.Count; ordinal++)
        {
            if (table.Columns[ordinal].Identity is not null) { identityOrdinal = ordinal; break; }
        }
        if (identityOrdinal >= 0)
        {
            for (int index = 0; index < targetOrdinals.Count; index++)
            {
                if (targetOrdinals[index] == identityOrdinal) { identityTargetIndex = index; break; }
            }
        }
        for (int rowIndex = 0; rowIndex < sourceRows.Count; rowIndex++)
        {
            var sourceRow = sourceRows[rowIndex];
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceRow.Length != targetOrdinals.Count)
            {
                throw new DatabaseException(
                    $"INSERT row has {sourceRow.Length} values but {targetOrdinals.Count} target columns.");
            }
            var values = new object?[table.Columns.Count];
            var assigned = new bool[table.Columns.Count];
            // Reserve a requested identity before any column's NOT NULL/default validation,
            // including when ALTER appended it after another required column. A failed row
            // consumes its generated value; explicit BY DEFAULT assignments never allocate.
            if (identityOrdinal >= 0 && (identityTargetIndex < 0 || valueRows is not null &&
                valueRows[rowIndex][identityTargetIndex].Kind == SqlBoundExpressionKind.DefaultItem))
            {
                values[identityOrdinal] = ResolveColumnDefault(table, identityOrdinal, defaults[identityOrdinal], cancellationToken);
                assigned[identityOrdinal] = true;
            }
            for (int i = 0; i < targetOrdinals.Count; i++)
            {
                if (valueRows is not null && valueRows[rowIndex][i].Kind == SqlBoundExpressionKind.DefaultItem)
                {
                    continue;
                }
                int ordinal = targetOrdinals[i];
                if (table.Columns[ordinal].Identity is { IsAlways: true })
                {
                    throw SqlPlanningException.IdentityViolation(table.Columns[ordinal].Name, ErrorMessages.IdentityInsertForbidden);
                }
                values[ordinal] = CoerceForColumn(sourceRow[i], table.Columns[ordinal]);
                assigned[ordinal] = true;
            }
            for (int ordinal = 0; ordinal < values.Length; ordinal++)
            {
                if (!assigned[ordinal])
                {
                    // The bound DEFAULT's value, or NULL, or the NOT NULL failure, for a column without one.
                    values[ordinal] = ResolveColumnDefault(table, ordinal, defaults[ordinal], cancellationToken);
                }
            }

            // Every column of a domain is assigned by an INSERT, its default included (phase E3).
            if (domainEvaluator is not null)
            {
                CheckDomains(bound, values, domainEvaluator);
            }

            rows.Add((SqlRowCodec.Encode(table, values, statement.Transaction.Sequence), values));
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
                return (DatabaseResult)new SqlQueryResult(DatabaseResultStatus.Success, rows.Count);
            }, durable: false, cancellationToken).ConfigureAwait(false);
        }
        catch (IndexUniqueViolationException exception)
        {
            throw TranslateUniqueViolation(table, exception);
        }
    }

    /// <summary>One default-resolution path for omitted INSERT columns and explicit DEFAULT assignments.</summary>
    private object? ResolveColumnDefault(SqlCatalogTable table, int ordinal, SqlBoundExpression? boundDefault, CancellationToken cancellationToken)
    {
        var column = table.Columns[ordinal];
        if (column.Identity is not null)
        {
            try
            {
                return CoerceForColumn(_catalog.AllocateIdentity(table), column);
            }
            catch (SqlCatalogException failure)
            {
                throw new SqlPlanningException("COHSQLE025", failure.Message, innerException: failure);
            }
        }
        return boundDefault is not null ? DefaultValue(boundDefault, cancellationToken) : ResolveDefault(column, null);
    }
}
