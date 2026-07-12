# Assimalign.Cohesion.Database — Design

The area root (architecture: [resources/Database/DESIGN.md](../../DESIGN.md)).
Everything here must be true for *all five* data models — anything model-specific
belongs in a model package. The root's job is to make engines substitutable at the
seams the platform builds on: the server serves any engine, the hosting layer
starts any engine, a client result looks the same regardless of the engine that
produced it.

## Why-this-not-that decisions

- **Engine → database → session → transaction as four contracts**, not one god
  interface. Each has a distinct lifetime and threading model: engines are
  process-long and thread-safe; databases are shared handles; sessions are
  cheap, single-threaded execution scopes; transactions are explicit ACID
  brackets inside a session. Collapsing them (an `ExecuteAsync` on the engine,
  say) would smuggle session state into a shared object.
- **Two execute seams on `IDatabaseSession`.** The typed seam
  (`ExecuteAsync(QueryRequest)`) is for in-process consumers that already speak a
  model's language objects (`SqlQueryRequest`). The **text seam**
  (`ExecuteAsync(string, parameters)`) exists for the wire protocol: the server
  receives statement *text* plus decoded parameter values and must stay
  model-agnostic — so the session, which knows its model, owns parsing. The
  alternative — the server referencing model language packages to build typed
  requests — would couple the one shared network front-end to every model and
  violate the area's composition rules. Each model implements the text seam with
  its own parser (`SqlDatabaseSession` → `SqlQueryRequest.FromSql`).
- **`DatabaseParseException` as a first-class error category.** Parse failures
  and execution failures have different wire error codes (`ParseFailure` vs
  `ExecutionFailure`) and different caller responses (fix the text vs inspect
  the data). A subtype of `DatabaseException` keeps existing `catch` blocks
  working while giving the server an exact mapping — better than string-matching
  messages or per-model exception knowledge in the server.
- **`QueryRequest`/`QueryResult` live in `Database.Execution`, not here.** The
  root aggregates the execution family rather than owning it: execution is its
  own child root with pipeline/context machinery the contract root has no
  business carrying.
- **Server contracts do not live here.** `IDatabaseServer` belongs to
  `Database.Server`; the engine contract root must stay consumable by embedded,
  in-process users that never open a socket.

## Error model

`DatabaseException` is the area root (inherits `Exception` per the area-scoped
exception rule). Child projects derive their own roots from it
(`ProtocolException`, `SqlCatalogException`, `DatabaseClientException`, …).
`DatabaseParseException` is the only semantic subtype the root itself defines,
because the parse-vs-execute distinction is part of the session contract.

## Lifecycle pattern

- Engines: `IAsyncDisposable` + `IDisposable`; disposal stops and releases all
  open databases.
- Sessions: disposing rolls back any active transaction (documented on the
  interface; sessions must never commit implicitly on dispose).
- Transactions: disposing an uncommitted transaction rolls it back.

## AOT posture

Contracts, enums, and value objects only — no reflection, no serialization.

## Non-goals

- No connection/network concepts (that is `Database.Server`/`Database.Client`).
- No DI or configuration surface (that is `Database.Hosting`'s seam alone).
- No model-specific request or result types — models subclass the
  `Database.Execution` family in their own packages.
