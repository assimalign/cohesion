using System;
using System.Collections.Generic;
using System.Threading;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;

if (args.Length != 2) { throw new ArgumentException("Usage: seed|verify directory"); }
await using var engine = GraphDatabaseEngine.Create(new()
{
    RootPath = args[1], CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
    PageWriteBackBatchSize = 1024, MaintenanceInterval = TimeSpan.FromHours(1)
});
if (args[0] == "seed")
{
    var db = (IGraphDatabase)await engine.CreateDatabaseAsync("crash");
    await using var session = await db.CreateSessionAsync();
    await session.ExecuteAsync("CREATE (a:Person {name:'a'})-[r:LINK]->(b:Person {name:'b'})");
    await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_name", "name");
    await session.BeginTransactionAsync();
    await session.ExecuteAsync("MATCH (a:Person {name:'a'}) DETACH DELETE a");
    await session.ExecuteAsync("CREATE (a:Person {name:'partial'})-[r:PARTIAL]->(b:Person {name:'partial2'})");
    foreach (var worker in engine.Workers)
    {
        if (worker.Kind == DatabaseEngineWorkerKind.PageWriteBack)
        { ((DatabaseEngineWorker)worker).RunIteration(CancellationToken.None); }
    }
    // End the process after page write-back without disposing the active transaction.
    Environment.Exit(0);
}
else if (args[0] == "verify")
{
    var db = (IGraphDatabase)await engine.OpenDatabaseAsync("crash");
    await using var session = await db.CreateSessionAsync();
    await using var result = (QueryResultSet)await session.ExecuteAsync("MATCH (a:Person {name:'a'})-[r:LINK]->(b) RETURN a,r,b");
    int count = 0;
    await foreach (var row in result.GetRowsAsync())
    {
        count++;
        if (row.GetValue(0) is not GraphNode a || a.Properties["name"] is not "a" ||
            row.GetValue(1) is not GraphRelationship || row.GetValue(2) is not GraphNode b || b.Properties["name"] is not "b")
        { throw new InvalidOperationException("Committed path was not recovered."); }
    }
    if (count != 1) { throw new InvalidOperationException("Expected one committed path."); }
    await using var all = (QueryResultSet)await session.ExecuteAsync("MATCH (a:Person) RETURN a");
    count = 0;
    await foreach (var row in all.GetRowsAsync()) { count++; }
    if (count != 2) { throw new InvalidOperationException("Partial nodes survived recovery."); }
    if ((await GraphSchema.Open(db, session).GetRelationshipTypesAsync()).Count != 1)
    { throw new InvalidOperationException("Uncommitted relationship metadata survived recovery."); }
    Console.WriteLine("Committed nodes, relationships, adjacency and index recovered; partial writes absent.");
}
else { throw new ArgumentException("Unknown fixture mode."); }
