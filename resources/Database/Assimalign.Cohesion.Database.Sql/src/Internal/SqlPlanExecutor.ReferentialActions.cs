using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlPlanExecutor
{
    // Constraint validation sees the statement's final row set. Lookups still use the
    // existing indexes and snapshot; pending replacements contribute their final keys.
    private IEnumerable<((PageId PageId, int SlotIndex) Location, object?[] Values)> FinalConstraintRows(
        SqlCatalogTable table, IReadOnlyList<string> columns, object?[] keys, SqlStatementContext statement,
        CancellationToken cancellationToken,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete>? deletions,
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>? updates, TransactionSnapshot? snapshot = null)
    {
        foreach (var row in FindConstraintRows(table, columns, keys, statement, cancellationToken, snapshot))
        {
            var key = (table.ObjectId, SqlRecordLocation.Pack(row.Location.PageId, row.Location.SlotIndex));
            if (deletions?.ContainsKey(key) != true && updates?.ContainsKey(key) != true) { yield return row; }
        }
        if (updates is null) { yield break; }
        foreach (var update in updates.Values)
        {
            if (update.Table.ObjectId == table.ObjectId && columns.Select((column, index) =>
                ValuesEqual(update.NewValues[FindColumnOrdinal(table, column)], keys[index],
                    table.Columns[FindColumnOrdinal(table, column)].Collation ?? _catalog.DefaultCollation)).All(equal => equal))
            {
                yield return (update.Location, update.NewValues);
            }
        }
    }

    private async Task<Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>> CollectReferentialUpdatesAsync(
        Dictionary<(ulong ObjectId, ulong Location), SqlConstraintDelete> deletions,
        SqlStatementContext statement, CancellationToken cancellationToken)
    {
        var updates = new Dictionary<(ulong ObjectId, ulong Location), SqlConstraintUpdate>();
        foreach (var deletion in deletions.Values)
        {
            foreach (var (child, constraint) in IncomingReferences(deletion.Table))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (constraint.OnDelete is not (SqlCatalogReferentialAction.SetNull or SqlCatalogReferentialAction.SetDefault)) { continue; }
                var keys = constraint.ReferencedColumns!.Select(column => deletion.Values[FindColumnOrdinal(deletion.Table, column)]).ToArray();
                if (keys.Any(value => value is null)) { continue; }
                var matches = FindConstraintRows(child, constraint.Columns, keys, statement, cancellationToken,
                    ConstraintCurrentSnapshot(statement)).ToList();
                var bound = _definitions.Get(child, cancellationToken);
                foreach (var match in matches)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var key = (child.ObjectId, SqlRecordLocation.Pack(match.Location.PageId, match.Location.SlotIndex));
                    if (deletions.ContainsKey(key)) { continue; }
                    if (!updates.TryGetValue(key, out var update))
                    {
                        await AcquireRowWriteLocksAsync(statement, child.ObjectId, [match.Location], cancellationToken).ConfigureAwait(false);
                        EnsureSnapshotSeesCascadeRow(child, match.Location, statement);
                        await AcquireIncomingReferenceIntentLocksAsync(child, statement, cancellationToken).ConfigureAwait(false);
                        await AcquireOutgoingReferenceIntentLocksAsync(child, statement, cancellationToken).ConfigureAwait(false);
                        EnsureCurrentDefinition(child);
                        update = new SqlConstraintUpdate(child, match.Location, match.Values, (object?[])match.Values.Clone(), []);
                        updates.Add(key, update);
                    }
                    foreach (string name in constraint.Columns)
                    {
                        int ordinal = FindColumnOrdinal(child, name);
                        if (constraint.OnDelete == SqlCatalogReferentialAction.SetDefault && child.Columns[ordinal].Identity is { IsAlways: true })
                        {
                            throw SqlPlanningException.IdentityViolation(name, ErrorMessages.IdentityUpdateForbidden);
                        }
                        object? value = constraint.OnDelete == SqlCatalogReferentialAction.SetNull ? null
                            : ResolveColumnDefault(child, ordinal, bound.DefaultValues[ordinal], statement, cancellationToken);
                        if (value is null && !child.Columns[ordinal].IsNullable) { throw Violation(child, constraint, keys); }
                        update.NewValues[ordinal] = CoerceForColumn(value, child.Columns[ordinal]);
                        update.Assigned.Add(ordinal);
                    }
                }
            }
        }

        var references = new List<SqlParentReference>();
        var released = new List<SqlParentReference>();
        foreach (var group in updates.Values.GroupBy(update => update.Table))
        {
            var table = group.Key;
            var bound = _definitions.Get(table, cancellationToken);
            foreach (var update in group)
            {
                CheckDomains(bound, update.NewValues, ExecutionEvaluator(null, cancellationToken), update.Assigned.ToArray());
                ValidateParentUpdate(table, update.OldValues, update.NewValues, statement, cancellationToken, deletions, updates);
            }
            ValidateRows(table, group.Select(update => update.NewValues).ToList(), statement, cancellationToken,
                references: references, deletions: deletions, updates: updates);
            CollectReleasedReferences(table, group.Select(update => update.OldValues).ToList(), released, statement, cancellationToken);
        }
        await AcquireParentRowLocksAsync(references, statement, cancellationToken).ConfigureAwait(false);
        await AcquireParentRowLocksAsync(released, statement, cancellationToken, validate: false).ConfigureAwait(false);
        // All row and reference locks precede unique-key locks and the physical bracket.
        foreach (var group in updates.Values.GroupBy(update => update.Table).OrderBy(group => group.Key.ObjectId))
        {
            var indexes = GetLiveIndexes(group.Key);
            var hashes = new List<ulong>();
            foreach (var update in group)
            {
                CollectUniqueKeyHashes(indexes, group.Key, update.OldValues, hashes);
                CollectUniqueKeyHashes(indexes, group.Key, update.NewValues, hashes);
            }
            await AcquireUniqueKeyLocksAsync(statement, group.Key.ObjectId, hashes, cancellationToken).ConfigureAwait(false);
        }
        return updates;
    }
}
