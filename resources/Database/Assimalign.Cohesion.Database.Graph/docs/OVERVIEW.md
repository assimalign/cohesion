# Graph database

`Assimalign.Cohesion.Database.Graph` is the embedded property-graph engine. It creates, opens,
enumerates and drops logical databases, opens database-bound sessions, and implements the frozen
node, relationship and traversal operations of `GraphDatabase`. A session executes the bounded GQL
subset described in [Graph.Language](../../Assimalign.Cohesion.Database.Graph.Language/docs/DESIGN.md).

```csharp
await using var engine = GraphDatabaseEngine.Create(new() { RootPath = "graphs" });
var graph = await engine.CreateDatabaseAsync("people");
await using var session = await graph.CreateSessionAsync();
await session.ExecuteAsync("INSERT (a:Person {name:'Ada'})-[r:KNOWS]->(b:Person {name:'Grace'})");
await GraphSchema.Open(graph, session).CreateIndexAsync("Person", "by_name", "name");
await using var result = await session.ExecuteAsync(
    "MATCH (a:Person {name:'Ada'})-[r:KNOWS]->(b) RETURN a,r,b");
```

`GraphSchema.Open` supplies session-bound label/type discovery, property metadata, ownership
enforcement and node-property index creation. `AddGraph((context, engine) => ...)`
captures construction through `IDatabaseApplicationBuilder` and returns that builder.
The callback runs at Build with the sealed `GraphDatabaseEngineBuilder`. Workers and
servers register as nested factories typed over `GraphDatabaseEngine`; the built engine
owns their products. Creating the engine starts its four built-in maintenance workers;
application Start starts the nested servers.

The engine, database, session, transaction, server and builder are sealed types; the first
five are leaves of the area root's bases (`DatabaseEngine`, `DatabaseInstance`,
`DatabaseSession`, `DatabaseTransaction`, `DatabaseServer`), which own the shared lifecycle,
the explicit-transaction state machine and their checks, so the typed members need no casts
(concrete-types plan, phase 4; DESIGN.md, "Concrete types").

A failed journal or data fsync takes the database offline: every later operation, in process
and over the server, is refused with `DatabaseOfflineException` (`COHDBG012`) until
`OpenDatabaseAsync` reopens it and recovery decides the unconfirmed commit (#1243).
`BufferPoolCapacity` (32 MiB), `CheckpointJournalSize` (256 MiB) and `CheckpointInterval`
(5 minutes) size the buffer pool and trigger checkpoints (#1254); a failed undo is retried on a
100 ms backoff (#1226). See DESIGN.md, "Storage operations".

The engine references the area root and Graph.Language, Graph.Catalog and Graph.Storage. The
storage and catalog compose the shared kernel. It is `net10.0`, AOT compatible, and uses neither
reflection nor `Microsoft.Extensions.*`. `GraphDatabaseServer` dispatches scalar GQL, mutations,
catalog `SHOW`, and path queries to the same engine. The separate NuGet-only
[Graph.Client](../../Assimalign.Cohesion.Database.Graph.Client/docs/OVERVIEW.md) package supplies
pooled scalar execution and path streaming. Security policy integration, replication and
compiled-schema provisioning remain outside this phase. See [DESIGN.md](DESIGN.md) for lifecycle,
isolation, traversal bounds and the disk format.

For real path results, pass `GraphPathsQueryRequest.FromGql(...)` to the existing session
`ExecuteAsync` method and read `GraphPathsQueryResult.Paths`. A single projected node becomes a
one-node path, a single relationship includes its stored endpoints, and
`MATCH p = (a)-[r:KNOWS]->(b) RETURN p` preserves the matched traversal order. Scalar and mutation
requests continue to use ordinary execution. Explicit transactions remain available through the
in-process session API; Graph wire transaction control is deliberately deferred. A statement that
fails inside an explicit transaction aborts the whole transaction: the session refuses further
statements with `COHDBG007` until the caller rolls back, and a commit fails without committing.
A read that names a label or relationship type the database does not have is not a failure: as in
Neo4j, the name matches nothing and the result carries a `COHDBG010` or `COHDBG011` warning, so a
probe such as `MATCH (n:Missing) RETURN n` keeps the transaction.

`GraphDatabaseEngine.CreateBuilder()` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.
