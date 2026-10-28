using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;

var builder = GraphDatabaseEngine.CreateBuilder("graph-aot");
builder.Functions.Add(GraphScalarFunction.Create<long, long>("developer_twice", value => checked(value * 2)));
builder.Functions.Add(GraphAggregateFunction.Create<long, long, long>("developer_sum", () => 0L,
    (state, value) => checked(state + value), state => state, _ => 8));
await using var engine = await builder.BuildAsync();
var database = await engine.CreateDatabaseAsync("app", CancellationToken.None);
await using var session = await database.CreateSessionAsync(CancellationToken.None);
var parameters = new Dictionary<string, object?> { ["xs"] = Enumerable.Range(0, 317).ToArray() };
int checks = 0;

var renderedPath = GraphQueryRequest.FromGql("MATCH p = SHORTEST 2 WALK GROUPS ((a WHERE a.k>0)->{0,3}(b) WHERE a.k<>b.k) RETURN p")
    .Statement.GqlExpression.Matches[0].ToString();
var renderedStatement = (GqlQueryStatement)new GqlQueryParser().Parse($"MATCH {renderedPath} RETURN p");
if (renderedStatement.Diagnostics.Any() || renderedStatement.GqlExpression.Matches[0].ToString() != renderedPath)
{ throw new InvalidOperationException("Path rendering smoke mismatch."); }
checks++;

await Check("RETURN developer_twice(21)", [[42L]]);
await Check("FOR v IN [1,2,2,NULL] RETURN developer_sum(v), developer_sum(DISTINCT v), count(*)", [[5L,3L,4L]]);
await Check("FOR v IN [] RETURN developer_sum(v),sum(v)", [[0L,0L]]);
await Check("RETURN abs(-7),char_length('A😀B')", [[7L,3L]]);
await Check("RETURN [x IN [1,2,3] WHERE x>1 | x*2][-1], size([1,2,3][..-1]), size([x IN [] | x])", [[6L,2L,0L]]);
await Check("LET x=99 RETURN [x IN [1,2] | [x IN [x+10] | x][0]][1]+x", [[111L]]);
await Check("INSERT (a:ComprehensionProperty {k:[x IN [1] | x+10][0]}) SET a.k=[x IN [a.k] | x+10][0] RETURN a.k", [[21L]]);
await Check("UNWIND [1,2,1] AS k MERGE (n:ComprehensionMerge {k:[x IN [k] | x*2][0]}) ON CREATE SET n.c=[x IN [k] | x+10][0] ON MATCH SET n.m=[x IN [k] | x+20][0] RETURN count(*)", [[3L]]);
await Check("MATCH (n:ComprehensionMerge) RETURN n.k,n.c,coalesce(n.m,0) ORDER BY n.k", [[2L,11L,21L],[4L,12L,0L]]);
await Check("RETURN 7", [[7L]]);
await Check("FOR x IN [1,1,1] MERGE (n:MergeSmoke {id:x}) ON CREATE SET n.hits=1 ON MATCH SET n.hits=n.hits+1 RETURN n.hits", [[3L],[3L],[3L]]);
await Check("MATCH (a:MergeSmoke {id:1}) MERGE (a)-[r:MergeEdge]-(b:MergeSmoke {id:2}) ON CREATE SET r.hit=1 ON MATCH SET r.hit=r.hit+1 RETURN r.hit", [[1L]]);
await Check("MERGE (:MergeSmoke {id:2})-[r:MergeEdge]-(:MergeSmoke {id:1}) ON MATCH SET r.hit=r.hit+1 RETURN r.hit", [[2L]]);
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
await Check("MATCH (a:N {k:1})-[rs:T]->{2}(b) RETURN size([r IN rs | type(r)]), [n IN [a,b] | n.k][1]", [[2L,3L]]);
await Check("MATCH (a:N)-[:T]->(b:N) RETURN a.k,b.k ORDER BY a.k", [[1L, 2L], [2L, 3L]]);
{
    var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(
        "MATCH p=(a:N {k:1})-[:T]->+(b:N) RETURN p LIMIT 1"), CancellationToken.None);
    if (paths.Paths.Count != 1 || paths.Paths[0].Relationships.Count != 1) { throw new InvalidOperationException("Path smoke mismatch."); }
    checks++;
}
foreach (string selector in new[] { "ANY", "ALL", "ANY SHORTEST", "ALL SHORTEST", "SHORTEST 2", "SHORTEST 2 GROUPS" })
{
    await Check($"MATCH p = {selector} ((a:N WHERE a.k=1)-[:T]->{{0,3}}(b:N WHERE b.k=3) WHERE path_length(p)>0) RETURN path_length(p)", [[2L]]);
    var selected = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql(
        $"MATCH p = {selector} (a:N {{k:1}}) ((x WHERE x.k<y.k)-[r:T WHERE type(r)='T']->(y)){{0,3}} (b:N {{k:3}}) RETURN p"), CancellationToken.None);
    if (selected.Paths.Count != 1 || selected.Paths[0].Relationships.Count != 2)
    { throw new InvalidOperationException("Selector path smoke mismatch."); }
    checks++;
}
await Check("MATCH p = SHORTEST 1 (a:N {k:1})-[:T]->{0,2}(m)-[:T]->{0,2}(b:N {k:3}) RETURN path_length(p)", [[2L]]);
await Refuse(new() { WorkUnits = 3 }, "CG05004");
await Refuse(new() { MemoryBytes = 1024 }, "CG05020");
await Refuse(new() { WorkUnits = 0 }, "CG08021");
await Check("RETURN 7", [[7L]]);
await session.ExecuteAsync("INSERT (s:ModeLoop)-[:LOOP]->(s)", cancellationToken: CancellationToken.None);
await Check("MATCH WALK (a:ModeLoop)-[:LOOP]->{0,3}(b) RETURN count(*)", [[4L]]);
await Check("MATCH TRAIL (a:ModeLoop)-[:LOOP]->{0,3}(b) RETURN count(*)", [[2L]]);
await Check("MATCH SIMPLE (a:ModeLoop)-[:LOOP]->{0,3}(b) RETURN count(*)", [[2L]]);
await Check("MATCH ACYCLIC (a:ModeLoop)-[:LOOP]->{0,3}(b) RETURN count(*)", [[1L]]);
await session.ExecuteAsync("BEGIN", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("INSERT (:Uncommitted)", cancellationToken: CancellationToken.None);
await Check("MATCH (n:Uncommitted) RETURN count(*)", [[1L]]);
await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
await Check("MATCH (n:Uncommitted) RETURN count(*)", [[0L]]);
await session.ExecuteAsync("START TRANSACTION", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("INSERT (:Committed)", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("COMMIT", cancellationToken: CancellationToken.None);
await Check("MATCH (n:Committed) RETURN count(*)", [[1L]]);
await session.ExecuteAsync("BEGIN TRANSACTION", cancellationToken: CancellationToken.None);
await session.ExecuteAsync("INSERT (:AliasPending)", cancellationToken: CancellationToken.None);
await Check("MATCH (n:AliasPending) RETURN count(*)", [[1L]]);
await session.ExecuteAsync("ROLLBACK", cancellationToken: CancellationToken.None);
await Check("MATCH (n:AliasPending) RETURN count(*)", [[0L]]);
var shortestStart = await database.CreateNodeAsync(session, ["Shortest"], cancellationToken: CancellationToken.None);
var shortestMiddle = await database.CreateNodeAsync(session, ["Shortest"], cancellationToken: CancellationToken.None);
var shortestEnd = await database.CreateNodeAsync(session, ["Shortest"], cancellationToken: CancellationToken.None);
var shortestIsolated = await database.CreateNodeAsync(session, ["Shortest"], cancellationToken: CancellationToken.None);
await database.CreateRelationshipAsync(session, shortestStart.Id, shortestMiddle.Id, "KNOWS", cancellationToken: CancellationToken.None);
await database.CreateRelationshipAsync(session, shortestEnd.Id, shortestMiddle.Id, "KNOWS", cancellationToken: CancellationToken.None);
var shortestSearch = new GraphShortestPathSearch(shortestStart.Id, shortestEnd.Id, GraphDirection.Both, "KNOWS");
if (await database.FindShortestDistanceAsync(session, shortestSearch, CancellationToken.None) != 2)
{ throw new InvalidOperationException("Shortest distance smoke mismatch."); }
checks++;
var shortestPath = await database.FindShortestPathAsync(session, shortestSearch, CancellationToken.None);
if (shortestPath?.Distance != 2 || shortestPath.Path.Nodes[2].Id != shortestEnd.Id ||
    shortestPath.Path.Relationships[1].From != shortestEnd.Id)
{ throw new InvalidOperationException("Shortest path smoke mismatch."); }
checks++;
if (await database.FindShortestDistanceAsync(session, shortestSearch with { Target = shortestIsolated.Id }, CancellationToken.None) is not null)
{ throw new InvalidOperationException("Disconnected shortest distance smoke mismatch."); }
checks++;
if (await database.FindShortestDistanceAsync(session, shortestSearch with { Target = shortestStart.Id }, CancellationToken.None) != 0)
{ throw new InvalidOperationException("Zero shortest distance smoke mismatch."); }
checks++;
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
