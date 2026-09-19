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
reflection nor `Microsoft.Extensions.*`. There is no Graph wire client, security policy integration,
replication or compiled-schema provisioning in this phase. See [DESIGN.md](DESIGN.md) for lifecycle,
isolation, traversal bounds and the disk format.

`GraphDatabaseEngine.CreateBuilder()` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.
