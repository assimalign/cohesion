# Graph public API

- `GraphDatabaseEngine.Create(name, options)` creates an operational engine, a sealed leaf of the root
  `DatabaseEngine`, whose lifecycle the base defines; `Workers` exposes the four maintenance
  duties. Dispose the engine to flush and release all databases. `CreateDatabaseAsync`,
  `OpenDatabaseAsync`, `GetDatabasesAsync` and `TryGetDatabase` return the typed `GraphDatabase`.
- `GraphDatabaseEngine.CreateBuilder(name)` returns the sealed `GraphDatabaseEngineBuilder`:
  `Options` (copied at build), `AddDatabase(name)` (opened or created by the build, never dropped
  while declared), `AddServer(options => ...)` and nested component factories typed over the
  engine, and `Build`/`BuildAsync`, for standalone construction or a hosting-aware engine factory.
  No options type carries the engine name.
- `GraphDatabase` (a `DatabaseInstance`) takes an explicit `GraphDatabaseSession` in its node,
  relationship and traversal operations; the session's `BeginTransactionAsync` returns
  `GraphDatabaseTransaction`. `GraphNode` and `GraphRelationship` are immutable record structs with
  scalar property maps and typed identities.
- `GraphTraversal` selects a start, direction, optional type and maximum depth. `TraverseAsync`
  yields distinct visited nodes in deterministic breadth-first order, excluding the start. It seeks
  selected type/direction adjacency ranges and fails with COHDBG004 past its physical work cap.
- `GraphQueryRequest.FromGql(text)` parses a request and throws `DatabaseParseException` for an
  error diagnostic. `GraphDatabaseSession.ExecuteAsync(string)` uses the same parser.
  RETURN may order by expressions/aliases with explicit null placement and page with
  OFFSET/SKIP and LIMIT. `GraphPathsQueryRequest` applies the same stage before returning
  `GraphPathsQueryResult.Paths`. Paging follows full materialization and every mutation;
  invalid dynamic counts fail before execution with `COHDBG013`.
- `GraphRequest.Parameters` supplies scalar, list and map bindings for `$name` and `@name` in
  WHERE/RETURN operands; pattern property maps remain scalar-only. Bare names match ordinally and case-sensitively after removing one sigil.
  Missing bindings fail planning with `COHDBG001`, invalid values with `COHDBG003`, and
  normalized-key collisions with `COHDBG014`. Runtime scalar types are preserved; lists snapshot
  to object?[] and maps to read-only IReadOnlyDictionary<string, object?>. Values permit depth 64,
  65,536 values and 16 MiB encoded bytes.
- A read that names a label or relationship type the database does not have succeeds: the name
  matches nothing, and the result's `Diagnostics` carries a `COHDBG010` (label) or `COHDBG011`
  (relationship type) warning. The read does not abort an explicit transaction.
- `GraphSchema.Open(database, session)` returns a sealed `GraphSchema` for label/type discovery,
  definition and property changes, and node-property index creation. It rejects foreign sessions.
  `GetIndexesAsync` returns `GraphSchemaResult<GraphIndexMetadata>`, a read-only list whose
  `Diagnostics` holds the `COHDBG010` warning for a label the database does not have.
- `AddGraph(name, engine => ...)` is an extension member on the root
  `IDatabaseApplicationBuilder`; it captures construction and returns the application builder.
  Its Build-time callback receives the `GraphDatabaseEngineBuilder` with model options and
  deferred, typed `AddWorker` / `AddServer` factories. Failed construction disposes completed
  products.
- `GraphDatabaseEngine.Servers` exposes nested servers whose lifetime the engine owns.
  Create/open/drop/lookup now accept `DatabaseName`, including its implicit string conversion.

`GraphValueCodec` encodes transient scalar/list/map values with explicit tags and the bounds above.
`GraphProtocol.CompositeValues` registers graph.composite-values; without negotiation, clients refuse
composite parameters locally and servers reject possible composite projections before writes.

See the [design](../../DESIGN.md) for transaction behavior, ownership exceptions and supported GQL.
