using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>Data-transaction receipts share the row MVCC/recovery protocol. The reserved chain is never a user table.</summary>
internal static class SqlScriptDataReceipt
{
    private static readonly SqlCatalogTable _table = new(ulong.MaxValue, "COHESION_INTERNAL", "SCRIPT_RECEIPTS",
        [new("name", new DatabaseTypeInfo(DatabaseType.String), false),
         new("hash", new DatabaseTypeInfo(DatabaseType.String), false),
         new("position", new DatabaseTypeInfo(DatabaseType.Int32), false)]);

    internal static async ValueTask AppendAsync(SqlDatabase database, TransactionContext transaction, SqlCatalogScript receipt, CancellationToken cancellationToken)
    {
        var record = SqlRowCodec.Encode(_table, [receipt.Name, receipt.ContentHash, receipt.CompletedStatements], transaction.Sequence);
        await database.Coordinator.ApplyStatementAsync(transaction, bracket =>
        {
            var location = database.DataStorage.InsertRow(bracket, _table.ObjectId, record);
            database.Coordinator.VersionStore.RecordCreated(transaction.Sequence, location.PageId, location.SlotIndex);
            return ValueTask.FromResult(true);
        }, durable: false, cancellationToken).ConfigureAwait(false);
    }

    internal static async ValueTask<HashSet<(string Name, string Hash, int Position)>> ReadAsync(SqlDatabase database, CancellationToken cancellationToken)
    {
        var transaction = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = transaction.Snapshot;
            var receipts = new HashSet<(string, string, int)>();
            using var iterator = database.DataStorage.GetUnitIterator(_table.ObjectId);
            while (iterator.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var values = SqlRowCodec.Decode(iterator.Current.Data.Span, _table, out var writer, out _, out _);
                if (snapshot.IsVisible(writer))
                {
                    receipts.Add(((string)values[0]!, (string)values[1]!, (int)values[2]!));
                }
            }
            return receipts;
        }
        finally { await database.Coordinator.RollbackAsync(transaction, CancellationToken.None).ConfigureAwait(false); }
    }
}
