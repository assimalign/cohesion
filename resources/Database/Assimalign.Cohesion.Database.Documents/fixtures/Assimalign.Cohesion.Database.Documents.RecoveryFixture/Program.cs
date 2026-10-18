using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Documents.Catalog;
using Assimalign.Cohesion.Database.Execution;

if (args.Length != 2) { throw new ArgumentException("Usage: seed|verify directory"); }
await using var engine = DocumentDatabaseEngine.Create("document-engine", new()
{
    RootPath = args[1],
    CheckpointInterval = TimeSpan.FromHours(1),
    PageWriteBackInterval = TimeSpan.FromHours(1),
    PageWriteBackBatchSize = 1024,
    MaintenanceInterval = TimeSpan.FromHours(1)
});
string original = "{\"marker\":7,\"rank\":1,\"nested\":{\"array\":[true,null,\"" + new string('x', 40000) + "\"]}}";
string updated = original.Replace("\"rank\":1,", "\"rank\":11,", StringComparison.Ordinal);
if (args[0] == "seed")
{
    var database = await engine.CreateDatabaseAsync("crash");
    await using var session = await database.CreateSessionAsync();
    await session.ExecuteAsync("CREATE COLLECTION items", cancellationToken: CancellationToken.None);
    var collection = await session.GetCollectionAsync("items", CancellationToken.None);
    await collection.PutAsync(session, "committed", Encoding.UTF8.GetBytes(original));
    await collection.PutAsync(session, "deleted", "{\"rank\":2}"u8.ToArray());
    await collection.PutAsync(session, "restored", "{\"rank\":3}"u8.ToArray());
    await collection.PutAsync(session, "upserted", "{\"rank\":4,\"discarded\":true}"u8.ToArray());
    await session.ExecuteAsync("CREATE INDEX by_rank ON items (rank)");
    await session.ExecuteAsync("CREATE UNIQUE INDEX unique_rank_marker ON items (rank, marker)");
    if ((await session.ExecuteAsync("DELETE FROM items WHERE rank = 2")).AffectedCount != 1)
    { throw new InvalidOperationException("Committed DELETE did not affect one document."); }
    if ((await session.ExecuteAsync("UPDATE items SET rank = rank + 10 WHERE rank = 1", cancellationToken: CancellationToken.None)).AffectedCount != 1)
    { throw new InvalidOperationException("Committed UPDATE did not affect one document."); }
    if ((await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('upserted', {'rank':14})", cancellationToken: CancellationToken.None)).AffectedCount != 1)
    { throw new InvalidOperationException("Committed UPSERT did not affect one document."); }
    await session.BeginTransactionAsync();
    if ((await session.ExecuteAsync("UPSERT INTO items (KEY, VALUE) VALUES ('upserted', {'rank':98})", cancellationToken: CancellationToken.None)).AffectedCount != 1)
    { throw new InvalidOperationException("Uncommitted UPSERT did not affect one document."); }
    if ((await session.ExecuteAsync("UPDATE items SET rank = 99 WHERE rank = 11", cancellationToken: CancellationToken.None)).AffectedCount != 1)
    { throw new InvalidOperationException("Uncommitted UPDATE did not affect one document."); }
    await collection.PutAsync(session, "partial", Encoding.UTF8.GetBytes(original));
    if ((await session.ExecuteAsync("DELETE FROM items WHERE rank = 3")).AffectedCount != 1)
    { throw new InvalidOperationException("Uncommitted DELETE did not affect one document."); }
    foreach (var worker in engine.Workers)
    {
        // A pass records its failure instead of throwing it (#1268); the fixture fails fast.
        if (worker.Kind == DatabaseEngineWorkerKind.PageWriteBack
            && worker is DatabaseEngineWorker guided && !guided.RunIteration(CancellationToken.None))
        {
            throw new InvalidOperationException("The page write-back pass failed.", guided.Fault);
        }
    }
    // Process termination skips disposal and rollback after physical write brackets.
    // Only the first logical commit can survive recovery.
    Environment.Exit(0);
}
else if (args[0] == "verify")
{
    var database = await engine.OpenDatabaseAsync("crash");
    await using var session = await database.CreateSessionAsync();
    var collection = await session.GetCollectionAsync("items");
    var document = await collection.GetAsync(session, "committed");
    if (document is null || Encoding.UTF8.GetString(document.Value.Content.Span) != updated)
    {
        throw new InvalidOperationException("Committed document did not survive process termination.");
    }
    var upserted = await collection.GetAsync(session, "upserted", CancellationToken.None);
    if (upserted is null || Encoding.UTF8.GetString(upserted.Value.Content.Span) != "{\"rank\":14}")
    { throw new InvalidOperationException("Committed UPSERT replacement did not recover exactly."); }
    if (await collection.GetAsync(session, "partial") is not null)
    {
        throw new InvalidOperationException("An uncommitted document survived process termination.");
    }
    await using var result = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) AS total FROM items i WHERE i.rank = @rank", new Dictionary<string, object?> { ["rank"] = 11 });
    await using var cursor = result.GetRowsAsync().GetAsyncEnumerator();
    if (!await cursor.MoveNextAsync() || cursor.Current.GetValue(0) is not long count || count != 1)
    {
        throw new InvalidOperationException("Recovered index does not match committed documents.");
    }
    if (await collection.GetAsync(session, "deleted") is not null || await collection.GetAsync(session, "restored") is null)
    { throw new InvalidOperationException("Recovered DELETE content visibility is incorrect."); }
    foreach (var (rank, expected) in new[] { (2, 0L), (3, 1L) })
    {
        await using var deletion = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM items WHERE rank = @rank",
            new Dictionary<string, object?> { ["rank"] = rank }, CancellationToken.None);
        await using var deletionCursor = deletion.GetRowsAsync(CancellationToken.None).GetAsyncEnumerator();
        if (!await deletionCursor.MoveNextAsync() || !Equals(deletionCursor.Current.GetValue(0), expected) ||
            await deletionCursor.MoveNextAsync())
        { throw new InvalidOperationException("Recovered DELETE index visibility is incorrect."); }
    }
    await using var missing = (DatabaseResultSet)await session.ExecuteAsync("SELECT nested.absent AS absent FROM items WHERE rank = 11 AND nested.absent IS MISSING", cancellationToken: CancellationToken.None);
    await using var missingCursor = missing.GetRowsAsync(CancellationToken.None).GetAsyncEnumerator();
    if (!await missingCursor.MoveNextAsync() || !missingCursor.Current.SupportsMissing ||
        !missingCursor.Current.IsMissing(0) || missingCursor.Current.IsNull(0) || missingCursor.Current.GetValue(0) is not null ||
        await missingCursor.MoveNextAsync())
    {
        throw new InvalidOperationException("Recovered document does not retain SQL++ MISSING semantics.");
    }
    await using var composite = (DatabaseResultSet)await session.ExecuteAsync("SELECT COUNT(*) FROM items WHERE rank = 11 AND marker = 7");
    await using var compositeRows = composite.GetRowsAsync().GetAsyncEnumerator();
    if (!await compositeRows.MoveNextAsync() || !Equals(compositeRows.Current.GetValue(0), 1L))
    { throw new InvalidOperationException("Recovered composite seek lost a committed row."); }
    try
    {
        await collection.PutAsync(session, "duplicate-probe", "{\"rank\":11,\"marker\":7}"u8.ToArray());
        throw new InvalidOperationException("Recovered unique index accepted a duplicate.");
    }
    catch (DocumentConstraintViolationException) { }
    Console.WriteLine("Committed JSON and index recovered; partial writes absent.");
}
else { throw new ArgumentException("Unknown fixture mode."); }
