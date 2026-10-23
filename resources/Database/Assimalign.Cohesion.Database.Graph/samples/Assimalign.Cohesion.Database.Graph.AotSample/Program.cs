using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Protocol;

await using var engine = GraphDatabaseEngine.Create("graph-aot", new());
var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
await using var session = await database.CreateSessionAsync(CancellationToken.None);
var parameters = new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 317).ToArray() };
int checks = 0;

await Check("RETURN 7", [[7L]]);
await Check("INSERT (a:W {k:1}) SET a.k=2 RETURN a.k", [[2L]]);
await session.ExecuteAsync("MATCH (a:W) DELETE a", cancellationToken: CancellationToken.None);
checks++;
await Check("FOR x IN $xs FOR y IN $xs RETURN count(*)", [[100489L]]);
await Check("FOR x IN $xs FOR y IN $xs RETURN x,y LIMIT 2", [[0L, 0L], [0L, 1L]]);
await Check("FOR x IN $xs FOR y IN $xs RETURN x,y ORDER BY x DESC,y DESC OFFSET 2 LIMIT 2", [[316L, 314L], [316L, 313L]]);
await Check("FOR x IN $xs FOR y IN $xs WITH x,y ORDER BY x DESC,y DESC OFFSET 2 LIMIT 2 RETURN x,y", [[316L, 314L], [316L, 313L]]);
await Check("FOR x IN $xs RETURN count(DISTINCT x),min(x),max(x)", [[317L, 0L, 316L]]);
await Check("FOR x IN [2,1,2] RETURN DISTINCT x ORDER BY x", [[1L], [2L]]);
await session.ExecuteAsync("INSERT (a:N {k:1})-[:T]->(b:N {k:2})-[:T]->(:N {k:3})", cancellationToken: CancellationToken.None);
await Check("MATCH (a:N)-[:T]->(b:N) RETURN a.k,b.k ORDER BY a.k", [[1L, 2L], [2L, 3L]]);
{
    var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(
        "MATCH p=(a:N {k:1})-[:T]->+(b:N) RETURN p LIMIT 1"), CancellationToken.None);
    if (paths.Paths.Count != 1 || paths.Paths[0].Relationships.Count != 1) { throw new InvalidOperationException("Path smoke mismatch."); }
    checks++;
}
await Refuse(new() { WorkUnits = 3 }, "CG05004");
await Refuse(new() { MemoryBytes = 1024 }, "CG05020");
await Refuse(new() { WorkUnits = 0 }, "CG08021");
await Check("RETURN 7", [[7L]]);
await session.ExecuteAsync("START TRANSACTION", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("INSERT (:Uncommitted)", cancellationToken: CancellationToken.None);
await Check("MATCH (n:Uncommitted) RETURN count(*)", [[1L]]);
await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
await Check("MATCH (n:Uncommitted) RETURN count(*)", [[0L]]);
await session.ExecuteAsync("START TRANSACTION", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("INSERT (:Committed)", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
await Check("MATCH (n:Committed) RETURN count(*)", [[1L]]);
Console.WriteLine($"Graph NativeAOT: {checks}/{checks} checks passed.");

async Task Check(string query, long[][] expected)
{
    await using var result = (DatabaseResultSet)await session.ExecuteAsync(query, parameters, CancellationToken.None);
    int count = 0;
    await foreach (var row in result.GetRowsAsync(CancellationToken.None))
    {
        if (count >= expected.Length) { throw new InvalidOperationException("Too many rows."); }
        for (int column = 0; column < expected[count].Length; column++)
        { if (Convert.ToInt64(row.GetValue(column)) != expected[count][column]) { throw new InvalidOperationException("Value mismatch."); } }
        count++;
    }
    if (count != expected.Length) { throw new InvalidOperationException("Too few rows."); }
    checks++;
}

async Task Refuse(GraphQueryLimits limits, string code)
{
    try
    {
        await session.ExecuteAsync(GraphQueryRequest.FromGql("FOR x IN $xs RETURN x", parameters, limits), CancellationToken.None);
        throw new InvalidOperationException("Expected budget refusal.");
    }
    catch (DatabaseException error) when (error.Diagnostics.Count == 1 && error.Diagnostics[0].Code == code) { checks++; }
}
