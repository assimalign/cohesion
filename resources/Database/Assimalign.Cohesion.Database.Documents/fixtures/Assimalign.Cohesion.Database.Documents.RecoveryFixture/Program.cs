using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Documents;
using Assimalign.Cohesion.Database.Execution;

if (args.Length != 2) { throw new ArgumentException("Usage: seed|verify directory"); }
await using var engine = DocumentDatabaseEngine.Create(new()
{
    RootPath = args[1],
    CheckpointInterval = TimeSpan.FromHours(1),
    PageWriteBackInterval = TimeSpan.FromHours(1),
    PageWriteBackBatchSize = 1024,
    MaintenanceInterval = TimeSpan.FromHours(1)
});
string original = "{\"rank\":1,\"nested\":{\"array\":[true,null,\"" + new string('x', 40000) + "\"]}}";
if (args[0] == "seed")
{
    var database = (IDocumentDatabase)await engine.CreateDatabaseAsync("crash");
    var collection = await database.CreateCollectionAsync("items");
    await using var session = await database.CreateSessionAsync();
    await collection.PutAsync(session, "committed", Encoding.UTF8.GetBytes(original));
    await session.ExecuteAsync("CREATE INDEX by_rank ON items (rank)");
    await session.BeginTransactionAsync();
    await collection.PutAsync(session, "committed", "{\"rank\":99}"u8.ToArray());
    await collection.PutAsync(session, "partial", Encoding.UTF8.GetBytes(original));
    foreach (var worker in engine.Workers)
    {
        if (worker.Kind == DatabaseEngineWorkerKind.PageWriteBack)
        {
            ((DatabaseEngineWorker)worker).RunIteration(CancellationToken.None);
        }
    }
    // Process termination skips disposal and rollback after physical write brackets.
    // Only the first logical commit can survive recovery.
    Environment.Exit(0);
}
else if (args[0] == "verify")
{
    var database = (IDocumentDatabase)await engine.OpenDatabaseAsync("crash");
    var collection = await database.GetCollectionAsync("items");
    await using var session = await database.CreateSessionAsync();
    var document = await collection.GetAsync(session, "committed");
    if (document is null || Encoding.UTF8.GetString(document.Value.Content.Span) != original)
    {
        throw new InvalidOperationException("Committed document did not survive process termination.");
    }
    if (await collection.GetAsync(session, "partial") is not null)
    {
        throw new InvalidOperationException("An uncommitted document survived process termination.");
    }
    await using var result = (QueryResultSet)await session.ExecuteAsync("SELECT COUNT(*) AS total FROM items i WHERE i.rank = @rank", new Dictionary<string, object?> { ["rank"] = 1 });
    await using var cursor = result.GetRowsAsync().GetAsyncEnumerator();
    if (!await cursor.MoveNextAsync() || cursor.Current.GetValue(0) is not decimal count || count != 1)
    {
        throw new InvalidOperationException("Recovered index does not match committed documents.");
    }
    Console.WriteLine("Committed JSON and index recovered; partial writes absent.");
}
else { throw new ArgumentException("Unknown fixture mode."); }
