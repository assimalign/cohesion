# Graph database

## Wire contract ownership

`Assimalign.Cohesion.Database.Graph.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (13 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.Graph`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


`Assimalign.Cohesion.Database.Graph` is the embedded property-graph engine. It creates, opens,
enumerates and drops logical databases, opens database-bound sessions, and implements the frozen
node, relationship and traversal operations of `GraphDatabase`. A session executes the bounded GQL
subset, including quantified paths under trail semantics with ISO {m,n}/+/* and Cypher *m..n, group lists, source-ordered MATCH/OPTIONAL MATCH with null extension, named scalar/list/map parameters, Boolean/arithmetic WHERE and RETURN expressions,
core element/string/math functions, CAST, Cypher conversions, transaction-start CURRENT_DATE/CURRENT_TIMESTAMP,
CASE, COALESCE, NULLIF, list/map constructors and field/index access,
SET/REMOVE scalar properties and node labels through MVCC replacements, literal-map replacement and merge,
COUNT, SUM, AVG, MIN, MAX and COLLECT_LIST (Cypher collect), with implicit or ISO GROUP BY,
RETURN DISTINCT/ALL, star, aliases and standalone RETURN, ORDER BY with explicit null placement,
OFFSET/SKIP and LIMIT, described in [Graph.Language](../../Assimalign.Cohesion.Database.Graph.Language/docs/DESIGN.md).

```csharp
await using var engine = GraphDatabaseEngine.Create("people-graph", new() { RootPath = "graphs" });
var graph = await engine.CreateDatabaseAsync("people");
await using var session = await graph.CreateSessionAsync();
await session.ExecuteAsync("INSERT (a:Person {name:'Ada'})-[r:KNOWS]->(b:Person {name:'Grace'})");
await GraphSchema.Open(graph, session).CreateIndexAsync("Person", "by_name", "name");
await using var result = await session.ExecuteAsync(
    "MATCH (a:Person {name:'Ada'})-[r:KNOWS]->(b) RETURN a,r,b");
```

`GraphSchema.Open` supplies session-bound label/type discovery, property metadata, ownership
enforcement and node-property index creation. `AddGraph(name, graph => ...)` captures
construction through `IDatabaseApplicationBuilder` and returns that builder. At Build the callback
configures the sealed `GraphDatabaseEngineBuilder`: its `Options` (copied at build), the databases
it declares (`AddDatabase(name)`: opened or created by the build, never dropped while declared), a
server from options (`AddServer(options => ...)`), and deferred worker and server factories typed
over `GraphDatabaseEngine`. The built engine owns their products. Creating the engine starts its
four built-in maintenance workers; application Start starts the nested servers.

The engine, database, session, transaction, server and builder are sealed types; the first
five are leaves of the area root's bases (`DatabaseEngine`, `DatabaseInstance`,
`DatabaseSession`, `DatabaseTransaction`, `DatabaseServer`), which own the shared lifecycle,
the explicit-transaction state machine and their checks, so the typed members need no casts
(concrete-types plan, phase 4; DESIGN.md, "Concrete types").

A failed journal or data fsync takes the database offline: every later operation, in process
and over the server, is refused with `DatabaseOfflineException` (`COHDBG012`) until
`OpenDatabaseAsync` reopens it and recovery decides the unconfirmed commit (#1243). A database
its holder disposed is forgotten once the close ends, and `OpenDatabaseAsync` opens it again with
its graph, in memory as on disk (owner decision 33).
`BufferPoolCapacity` (32 MiB), `CheckpointJournalSize` (256 MiB) and `CheckpointInterval`
(5 minutes) size the buffer pool and trigger checkpoints (#1254); a failed undo is retried on a
100 ms backoff (#1226). See DESIGN.md, "Storage operations".

The engine references the area root and Graph.Language. Its Catalog and Storage components
live in the same assembly and compose the shared kernel. It is `net10.0`, AOT compatible, and uses neither
reflection nor `Microsoft.Extensions.*`. `GraphDatabaseServer` dispatches scalar GQL, mutations,
catalog `SHOW`, and path queries to the same engine. The separate NuGet-only
[Graph.Client](../../Assimalign.Cohesion.Database.Graph.Client/docs/OVERVIEW.md) package supplies
pooled value execution with negotiated composite encoding and path streaming. Security policy integration, replication and
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

`GraphDatabaseEngine.CreateBuilder(name)` returns the same model builder for
standalone composition or the concrete hosting builder's build-aware engine
factory. This lets the consumer pass already resolved values and register nested
components while keeping the model package dependency-free.

`GraphDatabaseServerOptions.Listener` takes an `IDuplexConnectionListener`, including TCP and InMemory listeners. The server owns accepted duplex connections and their protocol I/O.

## Session execution (#1576, 2026-10-16)

`GraphDatabaseSession` derives from `DatabaseLanguageSession<GraphRequest>`. It offers typed
and text execution; a foreign-model request fails compilation. `GraphRequest` owns its typed
`Statement` and optional `Parameters` over the memberless `DatabaseRequest` root. Results
use the model-neutral `DatabaseResult` family. Lifecycle and explicit transactions remain
on `DatabaseSession`.

GQL linear composition executes LET, FILTER, FOR/UNWIND, RETURN ... NEXT and Cypher WITH over an eagerly materialized binding table. Projection boundaries retain only carried columns, including entity kinds and aggregate types; statements observe their own source-ordered writes. See [planning and bounds](DESIGN.md#planning-execution-and-bounds).


## Catalog component

The Catalog component is part of `Assimalign.Cohesion.Database.Graph`, with public types in `src/Catalog/` and helpers in `src/Internal/Catalog/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Catalog: Graph catalog

The `src/Catalog/` component provides durable discovery and definition metadata
for one logical graph database. It records node labels, relationship types, property keys, and
named node-property indexes. Labels and relationship types carry `DatabaseObjectOwner` and
an optional owning schema.

Open the catalog with `GraphCatalog.Open(storage, coordinator)` after the shared coordinator
has analyzed and scrubbed recovery. Reads take a `TransactionSnapshot`; writes take an active
`TransactionContext` and join that transaction without committing it. The engine holds the
definition lock and coordinates changes to graph data and physical indexes.

An initial definition can be marked schema-owned to exercise ownership enforcement.
Subsequent alteration, drop, property change, or index change throws
`DatabaseObjectLockedException` naming the definition, schema, and operation. Compiled graph
schema provisioning remains a separate feature.

The implementation depends on the Database root for ownership contracts, Graph.Storage for
the file-set facade, and the shared Storage, Transactions, and Types packages. It has no
reference to the Graph engine, Hosting, or ApplicationModel. Physical B+Trees and their
maintenance belong to Graph.Storage. See [DESIGN.md](DESIGN.md) for the binary format,
MVCC behavior, and division of responsibilities.



## Storage component

The Storage component is part of `Assimalign.Cohesion.Database.Graph`, with public types in `src/Storage/` and helpers in `src/Internal/Storage/`. Its namespaces are unchanged; it is not a separate project or assembly. The [model component boundaries](DESIGN.md#model-component-boundaries) govern its dependencies.

### Storage: Graph storage component

The `src/Storage/` component stores property-graph nodes, relationships,
endpoint adjacency, MVCC label postings and secondary node-property indexes. Filtered incident reads seek by
node, direction and catalog type GUID before decoding relationships; self-loops appear once. It targets .NET 10, is AOT
compatible, and uses explicit binary codecs without reflection.

Graph record and tree-registration format 4 adds catalog label GUIDs and label postings to
format 3, which shipped with #1532's typed adjacency and entity revisions. Opening an earlier
format fails with COHDBG017 before logical recovery scrub writes; recreate the database with
this engine. There is no upgrade path.

`GraphStorage` owns the data, journal and backup streams and delegates page allocation,
checksums and physical recovery to `Database.Storage`. Construct a `TransactionCoordinator`
from its `WriteAheadJournal` and `Records`, then use `GraphStore.Open` to obtain the sealed
`GraphStore`. Every mutation takes the caller's `TransactionContext` and leaves commit
or rollback to that caller. Reads take an immutable `TransactionSnapshot`.

The store exposes creation, MVCC replacement through `UpdateNodeAsync` and `UpdateRelationshipAsync`, lookup, restricted or cascading deletion, incident-edge
lookup, and exact node-property index creation/search/drop/listing. An incident-edge lookup is
a B+Tree range seek and does not scan every relationship. Property index searches return
scalar-verified candidates in identity order. Label scans seek catalog-ID/node-ID ranges and
decode only matching visible nodes; node create/update calls take the caller-resolved label IDs. The graph root maps storage DTO identities
to the frozen `GraphNodeId` and `GraphRelationshipId` contracts and plans traversals.

Dependencies are `Database.Storage`, `Database.Transactions` and `Database.Indexing`.
There is no dependency on the Graph root, its catalog, Hosting or ApplicationModel.
Owner-zero pages remain available to `Graph.Catalog` through the low-level record seam.

See [DESIGN.md](DESIGN.md) for the binary format, adjacency costs, numeric index
semantics, transaction lifecycle, recovery sequence and current limits.

Offline refusals and unconfirmed commits expose their existing model code through Diagnostics,
with unprefixed exception detail and the original cause. Failure tracing reads that carrier;
legacy wire error text keeps its existing code prefix. See DESIGN, structured failure codes (#1350).
