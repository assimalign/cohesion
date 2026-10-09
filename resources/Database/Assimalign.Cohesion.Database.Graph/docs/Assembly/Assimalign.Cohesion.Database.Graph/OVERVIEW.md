# Graph public API

- `GraphDatabaseEngine.Create(options)` creates an operational engine, a sealed leaf of the root
  `DatabaseEngine`, whose lifecycle the base defines; `Workers` exposes the four maintenance
  duties. Dispose the engine to flush and release all databases. `CreateDatabaseAsync`,
  `OpenDatabaseAsync`, `GetDatabasesAsync` and `TryGetDatabase` return the typed `GraphDatabase`.
- `GraphDatabaseEngine.CreateBuilder(name)` returns the sealed `GraphDatabaseEngineBuilder`: options
  and nested component factories typed over the engine, for standalone construction or a
  hosting-aware engine factory.
- `GraphDatabase` (a `DatabaseInstance`) takes an explicit `GraphDatabaseSession` in its node,
  relationship and traversal operations; the session's `BeginTransactionAsync` returns
  `GraphDatabaseTransaction`. `GraphNode` and `GraphRelationship` are immutable record structs with
  scalar property maps and typed identities.
- `GraphTraversal` selects a start, direction, optional type and maximum depth. `TraverseAsync`
  yields distinct visited nodes, excluding the start.
- `GraphQueryRequest.FromGql(text)` parses a request and throws `DatabaseParseException` for an
  error diagnostic. `DatabaseSession.ExecuteAsync(string)` uses the same parser.
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

See the [design](../../DESIGN.md) for transaction behavior, ownership exceptions and supported GQL.
