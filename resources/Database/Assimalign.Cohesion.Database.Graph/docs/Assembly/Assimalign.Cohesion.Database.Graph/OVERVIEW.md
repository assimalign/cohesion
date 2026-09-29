# Graph public API

- `GraphDatabaseEngine.Create(options)` creates an operational engine. Its lifecycle is defined by
  `IDatabaseEngine`; `Workers` exposes the four maintenance duties. Dispose the engine to flush
  and release all databases.
- `GraphDatabaseEngine.CreateBuilder()` exposes dependency-free options and nested component
  factories for standalone construction or a hosting-aware engine factory.
- `IGraphDatabase` retains its explicit session parameters. `GraphNode` and `GraphRelationship`
  are immutable record structs with scalar property maps and typed identities.
- `GraphTraversal` selects a start, direction, optional type and maximum depth. `TraverseAsync`
  yields distinct visited nodes, excluding the start.
- `GraphQueryRequest.FromGql(text)` parses a request and throws `DatabaseParseException` for an
  error diagnostic. `IDatabaseSession.ExecuteAsync(string)` uses the same parser.
- `GraphSchema.Open(database, session)` returns `IGraphSchema` for label/type discovery, definition
  and property changes, and node-property index creation. It rejects foreign sessions.
- `AddGraph((context, engine) => ...)` is an extension member on the root
  `IDatabaseApplicationBuilder`; it captures construction and returns the application builder.
  Its Build-time callback receives `IGraphDatabaseEngineBuilder` with model options and
  deferred `AddWorker` / `AddServer` factories. Failed construction disposes completed products.
- `IGraphStorageStrategy` optionally supplies storage create/open/drop and discovery, overriding
  RootPath. The engine owns returned storage; the caller owns the strategy.
- `GraphDatabaseEngine.Servers` exposes nested servers whose lifetime the engine owns.
  Create/open/drop/lookup now accept `DatabaseName`, including its implicit string conversion.

See the [design](../../DESIGN.md) for transaction behavior, ownership exceptions and supported GQL.
