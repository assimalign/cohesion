using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.RecoveryFixture;

if (args.Length != 2) { throw new ArgumentException("Usage: seed|verify directory"); }
await using var engine = GraphDatabaseEngine.Create("graph-engine", new()
{
    RootPath = args[1], CheckpointInterval = TimeSpan.FromHours(1), PageWriteBackInterval = TimeSpan.FromHours(1),
    PageWriteBackBatchSize = 1024, MaintenanceInterval = TimeSpan.FromHours(1)
});
if (args[0] == "seed")
{
    var db = await engine.CreateDatabaseAsync("crash");
    await using var session = await db.CreateSessionAsync();
    await session.ExecuteAsync("CREATE (a:Person {name:'a'})-[r:LINK]->(b:Person {name:'b'})");
    await GraphSchema.Open(db, session).CreateIndexAsync("Person", "by_name", "name");
    await session.ExecuteAsync("MATCH (a:Person {name:'a'}) SET a.committed=TRUE");
    await using var observer = await db.CreateSessionAsync();
    var before = await PersonNamesAsync(observer);
    await session.BeginTransactionAsync();
    await session.ExecuteAsync("MATCH (a:Person {name:'a'}) DETACH DELETE a");
    await session.ExecuteAsync("CREATE (a:Person {name:'partial'})-[r:PARTIAL]->(b:Person {name:'partial2'})");
    await session.ExecuteAsync("MATCH (b:Person {name:'b'}) SET b.name='b2'");
    await session.ExecuteAsync("MATCH (b:Person {name:'b2'}) REMOVE b:Person SET b:Other");
    if (!before.SequenceEqual(await PersonNamesAsync(observer)))
    { throw new InvalidOperationException(ErrorMessages.LabelSnapshotChanged); }
    foreach (var worker in engine.Workers)
    {
        // A pass records its failure instead of throwing it (#1268); the fixture fails fast.
        if (worker.Kind == DatabaseEngineWorkerKind.PageWriteBack
            && worker is DatabaseEngineWorker guided && !guided.RunIteration(CancellationToken.None))
        { throw new InvalidOperationException("The page write-back pass failed.", guided.Fault); }
    }
    // End the process after page write-back without disposing the active transaction.
    Environment.Exit(0);
}
else if (args[0] == "verify")
{
    var db = await engine.OpenDatabaseAsync("crash");
    await using var session = await db.CreateSessionAsync();
    await using var result = (DatabaseResultSet)await session.ExecuteAsync("MATCH (a:Person {name:'a'})-[r:LINK]->(b) RETURN a,r,b");
    int count = 0;
    await foreach (var row in result.GetRowsAsync())
    {
        count++;
        if (row.GetValue(0) is not GraphNode a || a.Properties["name"] is not "a" || a.Properties["committed"] is not true ||
            row.GetValue(1) is not GraphRelationship || row.GetValue(2) is not GraphNode b || b.Properties["name"] is not "b")
        { throw new InvalidOperationException("Committed path was not recovered."); }
    }
    if (count != 1) { throw new InvalidOperationException("Expected one committed path."); }
    await using var all = (DatabaseResultSet)await session.ExecuteAsync("MATCH (a:Person) RETURN a");
    count = 0;
    await foreach (var row in all.GetRowsAsync()) { count++; }
    if (count != 2) { throw new InvalidOperationException("Partial nodes survived recovery."); }
    if (!(await PersonNamesAsync(session)).SequenceEqual(new[] { "a", "b" }))
    { throw new InvalidOperationException(ErrorMessages.LabelSnapshotChanged); }
    await using var other = (DatabaseResultSet)await session.ExecuteAsync("MATCH (n:Other) RETURN n.name");
    await foreach (var row in other.GetRowsAsync())
    { throw new InvalidOperationException(ErrorMessages.UncommittedRelabelSurvived); }
    foreach (string query in new[] { "MATCH (b:Person {name:'b'}) RETURN b.name", "MATCH (b:Person) WHERE b.name STARTS WITH 'b' RETURN b.name" })
    {
        await using var b = (DatabaseResultSet)await session.ExecuteAsync(query);
        count = 0;
        await foreach (var row in b.GetRowsAsync()) { if (row.GetString(0) == "b") { count++; } }
        if (count != 1) { throw new InvalidOperationException(ErrorMessages.OriginalUpdateMissing); }
    }
    await using var b2 = (DatabaseResultSet)await session.ExecuteAsync("MATCH (b:Person {name:'b2'}) RETURN b.name");
    await foreach (var row in b2.GetRowsAsync()) { throw new InvalidOperationException(ErrorMessages.UncommittedUpdateSurvived); }
    if ((await GraphSchema.Open(db, session).GetRelationshipTypesAsync()).Count != 1)
    { throw new InvalidOperationException("Uncommitted relationship metadata survived recovery."); }
    Console.WriteLine("Committed nodes, relationships, adjacency, label postings and property index recovered; partial creates and relabels absent.");
}
else { throw new ArgumentException("Unknown fixture mode."); }

static async Task<List<string>> PersonNamesAsync(GraphDatabaseSession session, CancellationToken cancellationToken = default)
{
    await using var result = (DatabaseResultSet)await session.ExecuteAsync(
        "MATCH (n:Person) RETURN n.name ORDER BY n.name", cancellationToken: cancellationToken);
    var names = new List<string>();
    await foreach (var row in result.GetRowsAsync(cancellationToken)) { names.Add(row.GetString(0)!); }
    return names;
}
