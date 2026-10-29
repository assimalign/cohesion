# Graph database

## Wire contract ownership

`Assimalign.Cohesion.Database.Graph.Protocol` owns this model's wire messages, codecs, family singleton and transferred value shapes (13 public types). Both engine and remote client reference that one assembly. Its public namespace and RootNamespace equal its project name. Engine requests, planning, catalog, storage and server lifecycle stay in `Assimalign.Cohesion.Database.Graph`. This extraction preserves wire bytes and validation; moved public types intentionally change namespace and assembly identity with no forwarders.


`Assimalign.Cohesion.Database.Graph` is the embedded property-graph engine. It creates, opens,
enumerates and drops logical databases, opens database-bound sessions, and implements the frozen
node, relationship and traversal operations of `GraphDatabase`. A session executes the bounded GQL
subset, including WALK/TRAIL/SIMPLE/ACYCLIC path modes (implicit TRAIL), quantified paths with ISO {m,n}/+/* and Cypher *m..n, group lists, source-ordered MATCH/OPTIONAL MATCH with null extension, named scalar/list/map parameters, Boolean/arithmetic WHERE and RETURN expressions,
core element/string/math functions, CAST, Cypher conversions, transaction-start CURRENT_DATE/CURRENT_TIMESTAMP,
CASE, COALESCE, NULLIF, correlated EXISTS/NOT EXISTS braced patterns, list/map constructors, lexical list comprehensions, ordered reduce, ALL/ANY/NONE/SINGLE list predicates, open-ended slices and field/index access,
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

For a native shortest distance, use the typed API with database-local node IDs:

```csharp
int? distance = await graph.FindShortestDistanceAsync(session,
    new GraphShortestPathSearch(startId, targetId, GraphDirection.Both, "KNOWS"));
GraphShortestPathResult? shortest = await graph.FindShortestPathAsync(session,
    new GraphShortestPathSearch(startId, targetId, GraphDirection.Both, "KNOWS"));
```

Null means disconnected or an absent endpoint. A visible node to itself has distance zero.
The path result includes the actual distance and ordered entities; ties use the smallest
relationship-identity sequence. Search runs in one statement snapshot with
O(visited vertices + frontier) state, cancellation, and configurable memory/work limits through the search's
`Limits`. Exhaustion fails the statement. The defaults are the engine's 256 MiB and 1,000,000
work units; path materialization also consumes these budgets. See [typed shortest search](DESIGN.md#typed-shortest-distance-and-path-1536-gq-sp1).
GQL selectors ANY/ALL, ANY/ALL SHORTEST and counted shortest paths/groups now parse, bind, execute and
EXPLAIN with endpoint-pair partitions and separate search prefilters
under the statement work/memory budgets; see [selector execution](DESIGN.md#gql-path-selectors-execution-1734-gq-sp2sp3).

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
and over the server, is refused with `DatabaseOfflineException` (`CG06012`) until
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
in-process session API and BEGIN [TRANSACTION] / ISO START TRANSACTION / COMMIT / ROLLBACK over Execute. A statement that
fails inside an explicit transaction aborts the whole transaction: the session refuses further
statements with `CG04007` until the caller rolls back, and a commit fails without committing.
A read that names a label or relationship type the database does not have is not a failure: as in
Neo4j, the name matches nothing and the result carries a `CG01010` or `CG01011` warning, so a
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

GQL linear composition executes LET, FILTER, FOR/UNWIND, RETURN ... NEXT and Cypher WITH over streaming owned slot bindings for reads and bounded materialized mutation-target tables. Projection boundaries retain only carried columns, including entity kinds and aggregate types; statements observe their own source-ordered writes. See [planning and bounds](DESIGN.md#planning-execution-and-bounds).


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
endpoint adjacency, MVCC label and relationship-type postings and secondary node-property indexes. Filtered incident reads seek by
node, direction and catalog type GUID before decoding relationships; self-loops appear once. It targets .NET 10, is AOT
compatible, and uses explicit binary codecs without reflection.

Graph record and tree-registration format 5 requires relationship-type postings (#1633),
building on format 4's catalog label GUIDs and label postings and format 3's typed adjacency
and entity revisions. Type-only fixed relationship patterns use these postings as planner anchors,
competing with label scans and property seeks by advisory estimates. Directed and undirected
patterns, type unions and statement-local changes retain snapshot visibility and residual checks.
EXPLAIN reports RelationshipTypeScan, its selected types and edge ordinal, and the estimated
candidate count. The posting edge requires no incident expansion; unrelated nodes and edges add
no candidate work. Opening an earlier
format fails with CG06017 before logical recovery scrub writes; recreate the database with
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

## Dedicated test actors (#1285)

Deadline-dependent peers and busy test drivers use the linked Storage test helper's LongRunning threads. Ordinary async driver continuations stay on their actor scheduler, without changing SynchronizationContext. Existing deadlines of 25 seconds or more are hang guards. Runner settings impose no parallel-thread cap. See the [scheduling audit](../../Assimalign.Cohesion.Database.Storage/docs/TEST-HYGIENE-AUDIT.md) for every original Task.Run site, the exact journal counters and the physical index-work guards.

## Index history reclamation (#1195)

The shared coordinator's purge pass reclaims safe dead B-tree entries in bounded batches;
unique inserts also prune their key's history while reusing a single descent. Old snapshot
views remain valid. Empty-leaf reclamation remains deferred, with no format, wire or language
change. The callback follows the current manager after catalog/store reattachment.

## Cached record integrity

A present graph record with an unknown kind or zero identity fails as corruption with its
page and slot; it cannot become an absent node through cached-reference cleanup. A valid
replacement identity still invalidates the old reference. This fixes the defect found during
#1372 without changing graph format 4, public signatures or diagnostic code CK06003.


## Confirmed decode failures (#1372)

Storage's D1 seqlock confirms private record copies before models decode them. Malformed
copies report the existing corruption code with page and slot. Direct catalog APIs retain
their model exception type and cause; engine boundaries use the common corruption exception. The shared version store rejects records shorter than its
16-byte stamp header and retains failed undo/prune work for retry. StorageCorruptionException
adds a record-scoped constructor and nullable SlotIndex. Formats and wire frames are unchanged.

## Query resource budgets (#1533)

Read LIMIT without ORDER BY can stop upstream expansion; grouping consumes rows incrementally,
and ordered finite pages retain only OFFSET + LIMIT candidates. Final result rows/paths remain
materialized until statement completion. QueryMemoryBudget defaults to 256 MiB and QueryWorkBudget
to 1,000,000 physical units after relationship type/direction selection; FOR elements also consume
work. Engines can explicitly set higher positive ceilings. GraphQueryLimits supplies optional lower
MemoryBytes/WorkUnits to the three-argument request constructors/factories or GraphTraversal.Limits.
Remote Execute statements use server ceilings; wire payloads are unchanged. Memory exhaustion is
CG05020, invalid limits CG08021, and work/hop exhaustion CG05004. The default admits 100,000
one-property SET, FOR/INSERT, entity RETURN and QueryPaths rows; wider retained state reduces capacity.
TraverseAsync also enforces these ceilings, including the newly bounded 1,000,000 work units.
Containers and supported immutable leaves, including boxed scalars, are counted once across
operators. Root bookkeeping uses one combined ledger grant. Every ownership lease is released on completion or failure.

A bounded per-session cache reuses only cleared accounting buffers; large high-water workspaces
are discarded and disposal drops the cache. Retained values and budget ownership do not escape
into that cache. The unchanged TCP path-allocation test guards the combined engine/client cost.

All five server-options leaves inherit DatabaseServerOptions. Database.Tcp provides generic
Listen(Uri), retaining the concrete options type, and is public in App.Database (#1391, D7 B).
The SQL-specific TCP integration is removed; import Assimalign.Cohesion.Database for the helper.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.
Shared Engine source uses the consuming assembly's generated accessor through the internal `EngineErrorMessages` binding; no public resource accessor or cross-assembly internal access is introduced.

## Whole-pattern MERGE (#1735)

MERGE matches one complete fixed path or creates all its unbound parts, then runs ON MATCH or
ON CREATE SET. It supports bound nodes/relationships, undirected matching, writer overlays and
retryable stale-Snapshot detection through pattern locks. See the language DIALECT and this
project's DESIGN for validation, counts, concurrency and EXPLAIN behavior.

## Graph diagnostic conventions (#1470)

Exception and diagnostic templates use the project's build-generated `ErrorMessages` resources.
Parameterized templates format with the current culture; abort/cancellation templates used in
normal cleanup are cached. The [Database error-code registry](../../Assimalign.Cohesion.Database/docs/ERROR-CODES.md)
assigns this family its two-letter prefix and categorized five-digit identities.
Existing SQLSTATE meanings, numeric wire error kinds, frame layouts and disk formats are preserved.
Structured diagnostics keep their code outside the message. Compiler diagnostic identifiers and
standard .NET argument guards retain their own contracts.


## Reusable conflict throw member (#1470)

Catalog conflict callers use the internal static extension member
`DatabaseTransactionAbortedException.ThrowGraphConflict()`. The root Database assembly owns the
exception; Graph retains its cached resource and structured `CG04007` diagnostic. The message
contains no code prefix. Exception identity, diagnostic severity and catalog conflict behavior
are preserved, with no public API, wire, storage-format, event or counter change.


## Shared validation message completion (#1470)

Engine option requirements and portable database-name refusal reasons, including their rule
text, resolve through each consuming model's generated internal resources. Valid name checks
still avoid resource lookup; the engine-description template is cached for construction.
Existing message text, parameter names, refused values and pre-I/O validation order remain
unchanged. No public API, error identity, SQLSTATE, storage or wire format, event or counter changes.
Local predicate guards remain with the policies they validate; reusable unconditional catalog
throws use exception-type extension members. Private cold comparison and location-attachment
paths retain their allocation and exception-identity behavior.

## Explicit private execution state

Private execution accounting uses explicit constructors, with unchanged lease ownership, budgets and path results. Internal helpers follow the folder and asynchronous naming conventions; see [DESIGN.md](DESIGN.md).

## Direct catalog diagnostics

`GraphCatalogException.Diagnostics` is non-null and preserves the existing catalog error identities
without changing exception ancestry or message text. See [DESIGN.md](DESIGN.md#direct-catalog-diagnostic-carriers)
for producer codes and the legacy public-constructor contract.

## Shared lifecycle diagnostic completion

Duplicate logical database/storage creation carries CK03001, whether the database is open or
represented only by its closed files. Ordinary logical absence remains the precise
DatabaseNotFoundException and carries CK03002. Each diagnostic has Error severity, the unchanged
unprefixed exception message and no statement span. Existing coded interrupted-creation failures
and original causes are retained. No ownership, file mutation, lifecycle ordering, format or
protocol message-kind change accompanies these diagnostics.

## Model failure producer completion

Session-state, result-row and model-specific refusals expose structured codes while preserving
their exception types and detail. The [producer table](DESIGN.md#model-failure-producer-completion)
lists each condition. Successful execution, transaction ownership, storage formats and wire
layouts are unchanged; the existing wire formatter includes an error code once.

The result-header size guard uses the same `CG02013` value-limit diagnostic as row-size
validation, retaining the existing limit detail and ExecutionFailure category. A synthetic
header regression exercises the guard directly through the existing runtime test seam;
ordinary projection admission and the 16 MiB cap are unchanged.
Graph handshake refusals and the CK07001 overflow notice bind their existing wording through
resources. Protocol categories, authentication/version negotiation and the overflow notice's
invariant formatting and published limits remain unchanged.

## Debugger inspection (#1470)

Graph debugger inspection uses stored metadata without traversing bindings, enumerating results or expanding query/session state.

Eligible types follow the [Database conventions](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md) debugger display/proxy rules. Views are private and read-only; inspection does not change the public API, execution paths, error contracts, storage formats or protocol bytes.

## Developer functions

Add immutable scalar and aggregate functions through `GraphDatabaseEngine.CreateBuilder(...).Functions`
before BuildAsync. The engine's frozen Functions catalog contains native and developer functions,
with immutable GetOverloads results. Typed factories support NativeAOT; protected cores support
context-aware callbacks and multiple aggregate inputs without new interfaces.

Ordinary GQL requests and wire calls resolve registered names automatically. Function result
metadata declares nested entity/list/map shapes before writes; aggregate retained-state reports
participate in the existing statement memory budget. Unexpected callback failures use CG05021,
while native error behavior and infrastructure exception identity remain intact. Registrations
are engine-local composition only; disk and wire formats are unchanged. Named-call planning
maps are allocated lazily; native enum-only statements share immutable empty binding maps.
