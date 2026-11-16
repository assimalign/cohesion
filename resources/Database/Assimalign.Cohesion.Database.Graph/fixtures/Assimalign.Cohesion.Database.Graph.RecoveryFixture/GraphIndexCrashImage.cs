using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.RecoveryFixture;

internal static class GraphIndexCrashImage
{
    internal static async Task SeedAsync(string root, bool text, bool revision)
    {
        // Seed with the engine, then leave a real logical writer behind in the file set.
        await using (var engine = GraphDatabaseEngine.Create("index-crash", new() { RootPath = root }))
        {
            var database = await engine.CreateDatabaseAsync("image", CancellationToken.None);
            await using var session = await database.CreateSessionAsync(CancellationToken.None);
            await session.ExecuteAsync("INSERT (:N {k:1,p:'oldabc'}),(:Other {k:99,p:'keep'})", cancellationToken: CancellationToken.None);
            await session.ExecuteAsync(text ? "CREATE TEXT INDEX t FOR (n:N) ON (n.p)" : "CREATE INDEX t FOR (n:N) ON (n.p)", cancellationToken: CancellationToken.None);
        }
        string directory = Path.Combine(root, "image");
        using var storage = GraphStorage.Open(StorageStream.FromFile(Path.Combine(directory, "graph.dat"), FileMode.Open, FileShare.Read),
            StorageStream.FromFile(Path.Combine(directory, "graph.log"), FileMode.Open, FileShare.Read),
            StorageStream.FromFile(Path.Combine(directory, "graph.bak"), FileMode.Open, FileShare.Read), checkpointOnOpen: false);
        await using var coordinator = new TransactionCoordinator(storage, storage.WriteAheadJournal, storage.Records);
        var catalog = GraphCatalog.Open(storage, coordinator);
        var store = GraphStore.Open(storage, coordinator);
        var context = await coordinator.BeginAsync(IsolationLevel.Snapshot, CancellationToken.None);
        ulong id = 0;
        StoredGraphNode node = default;
        using (var records = storage.GetUnitIterator(2))
        {
            while (records.MoveNext())
            {
                var bytes = records.Current.Data.Span;
                ulong candidate = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(18));
                if (bytes[16] == 3) { id = candidate; }
                else if (bytes[16] == 1 && store.FindNode(candidate, context.Snapshot) is { } found && found.Labels.Contains("N")) { node = found; }
            }
        }
        if (id == 0 || node.Id == 0) { throw new InvalidOperationException("Expected seeded node and index."); }
        if (revision)
        {
            var label = catalog.FindLabel("N", context.Snapshot) ?? throw new InvalidOperationException("Expected seeded label.");
            await store.UpdateNodeAsync(node.Id, node.Labels, new Dictionary<string, object?> { ["k"] = 1L, ["p"] = "uncommitted" },
                new Dictionary<string, Guid> { ["N"] = label.Id }, context, CancellationToken.None);
        }
        else
        {
            var index = catalog.FindIndex("t", context.Snapshot) ?? throw new InvalidOperationException("Expected seeded index.");
            await catalog.DeleteIndexAsync(index.LabelId, "t", context, CancellationToken.None);
            if (!text) { await store.DropIndexAsync("N", "p", context, CancellationToken.None); }
            else
            {
                // The public store drop is exact-only. Stamp the text definition with the
                // same deleter as the real catalog DROP; recovery reads these physical stamps.
                Mutate(storage, 2, id, bytes => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), context.Sequence.Value));
            }
        }
        Mutate(storage, revision ? 2UL : 1UL, id, bytes =>
        {
            if (revision) { BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(26), 2); }
            else { BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(26), -BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(26))); }
        });
        storage.WriteBackDirtyPages(int.MaxValue);
        storage.WriteAheadJournal.Flush(forceDurable: true);
        Console.WriteLine("index-owner=" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // No transaction rollback, engine close, storage disposal or checkpoint follows.
        Environment.Exit(0);
    }

    private static void Mutate(GraphStorage storage, ulong owner, ulong id, Action<byte[]> mutation)
    {
        using var records = storage.GetUnitIterator(owner);
        while (records.MoveNext())
        {
            var record = records.Current; byte[] bytes = record.Data.ToArray();
            if (bytes[16] != (owner == 2 ? 3 : 4) || BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(18)) != id) { continue; }
            mutation(bytes);
            using var bracket = storage.BeginTransaction();
            storage.UpdateEntry(bracket, record.PageId, record.SlotIndex, bytes); bracket.Commit(); return;
        }
        throw new InvalidOperationException("Expected physical index record.");
    }
}
