# Graph public API

- `GraphDatabaseEngine.Create(options)` creates an operational engine. Its lifecycle is defined by
  `IDatabaseEngine`; `Workers` exposes the four maintenance duties. Dispose the engine to flush
  and release all databases.
- `IGraphDatabase` retains its explicit session parameters. `GraphNode` and `GraphRelationship`
  are immutable record structs with scalar property maps and typed identities.
- `GraphTraversal` selects a start, direction, optional type and maximum depth. `TraverseAsync`
  yields distinct visited nodes, excluding the start.
- `GraphQueryRequest.FromGql(text)` parses a request and throws `DatabaseParseException` for an
  error diagnostic. `IDatabaseSession.ExecuteAsync(string)` uses the same parser.
- `GraphSchema.Open(database, session)` returns `IGraphSchema` for label/type discovery, definition
  and property changes, and node-property index creation. It rejects foreign sessions.
- `AddGraphDatabase` is an extension member on the root `IDatabaseApplicationBuilder` and returns
  the registered engine. Registration failure disposes the engine before propagating the error.

See the [design](../../DESIGN.md) for transaction behavior, ownership exceptions and supported GQL.
