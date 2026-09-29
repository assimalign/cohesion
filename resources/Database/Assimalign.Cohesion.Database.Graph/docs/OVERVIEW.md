# Graph database

`Assimalign.Cohesion.Database.Graph` is the embedded property-graph engine. It creates, opens,
enumerates and drops logical databases, opens database-bound sessions, and implements the frozen
`IGraphDatabase` node, relationship and traversal operations. A session executes the bounded GQL
subset described in [Graph.Language](../../Assimalign.Cohesion.Database.Graph.Language/docs/DESIGN.md).

```csharp
await using var engine = GraphDatabaseEngine.Create(new() { RootPath = "graphs" });
var graph = (IGraphDatabase)await engine.CreateDatabaseAsync("people");
await using var session = await graph.CreateSessionAsync();
await session.ExecuteAsync("INSERT (a:Person {name:'Ada'})-[r:KNOWS]->(b:Person {name:'Grace'})");
await GraphSchema.Open(graph, session).CreateIndexAsync("Person", "by_name", "name");
await using var result = await session.ExecuteAsync(
    "MATCH (a:Person {name:'Ada'})-[r:KNOWS]->(b) RETURN a,r,b");
```

`GraphSchema.Open` supplies session-bound label/type discovery, property metadata, ownership
enforcement and node-property index creation. `AddGraph((context, engine) => ...)`
captures construction through `IDatabaseApplicationBuilder` and returns that builder.
The callback runs at Build with `IGraphDatabaseEngineBuilder`, whose options include
an optional borrowed `IGraphStorageStrategy`. Workers and servers register as nested
factories; the built engine owns their products. Creating the engine starts its four
built-in maintenance workers; application Start starts the nested servers.

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
in-process session API; Graph wire transaction control is deliberately deferred.

`GraphDatabaseEngine.CreateBuilder()` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.
