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
- **Background workers are engine-owned objects hosts *schedule*, not host
  services engines *implement*** (#902). `IDatabaseEngineWorker` (with the guided
  base `DatabaseEngineWorker`) exposes each durability/maintenance loop — kind,
  cadence, a blocking pump (`Run`) for dedicated threads, and a bounded step
  (`RunIteration`) for timer loops — plus an atomic claim handshake:
  `IDatabaseEngine.StartAsync` self-schedules every unclaimed worker, a host claims
  before start to drive workers on its own execution menu, and a worker therefore
  never runs twice concurrently. The rejected alternative — engines implementing the
  hosting library's service contracts directly — would invert the dependency (the
  kernel referencing the hosting layer) and strand embedded consumers, who have no
  host to run services (R10). The two pump shapes exist because the execution menu
  has two members: a dedicated thread wants one blocking call frame; a pooled timer
  wants a bounded pass and owns the wait itself. Workers are synchronous by design —
  every body is storage I/O (fsync, page writes, checkpoint), which has no async
  fast path.
- **Server *contracts* live here; the server *runtime* does not** (owner
  decision, 2026-07-12 — reverses the earlier "server contracts do not live
  here" non-goal). `IDatabaseServer`/`IDatabaseServerSession` are abstractions
  over root concepts only (`IDatabaseEngine`, `IDatabaseSession`,
  `ProtocolVersion`), and the hosting-isolation rule (COHRES001) makes the
  hosting module unreferenceable by area libraries — without the seam here, no
  feature library (quotas, health, a future `Database.Testing` factory) could
  even *name* the server. The runtime implementation stays in
  `Database.Hosting`; embedded, in-process users still pay for nothing but two
  interfaces and a two-`ushort` value type.
- **`ProtocolVersion` (the value type) lives here; the version the wire speaks
  does not.** The struct is `IDatabaseServerSession` vocabulary, so it moves
  with the seam. `ProtocolVersion.Current` — "the version this assembly
  implements" — is published by `Database.Protocol` as a static extension
  member, keeping the claim with the implementation that makes it true.

## Error model

`DatabaseException` is the area root (inherits `Exception` per the area-scoped
exception rule). Child projects derive their own roots from it
(`ProtocolException`, `SqlCatalogException`, `DatabaseClientException`, …).
`DatabaseParseException` is the only semantic subtype the root itself defines,
because the parse-vs-execute distinction is part of the session contract.

## Lifecycle pattern

- Engines: `StartAsync`/`StopAsync` are part of the root contract (added with
  #902 — previously only the concrete engines exposed them, which meant a host
  could *serve* engines but not *drive* them generically; `Database.Hosting`
  and `Database.Embedded` must align engine lifecycle with their own without
  knowing concrete types). Start is idempotent while `Running`; stop is a no-op
  when not `Running`; a stopped engine may start again. Stop quiesces
  background workers, durably flushes, and closes every open database —
  committed work is durable when `StopAsync` completes. State transitions ride
  the existing `EngineState` enum (`Idle`/`Stopped` → `Starting` → `Running` →
  `Stopping` → `Stopped`; `Faulted` is terminal for start).
- Engines: `IAsyncDisposable` + `IDisposable`; disposal stops and releases all
  open databases.
- Sessions: disposing rolls back any active transaction (documented on the
  interface; sessions must never commit implicitly on dispose).
- Transactions: disposing an uncommitted transaction rolls it back.

## AOT posture

Contracts, enums, and value objects only — no reflection, no serialization.

## Non-goals

- No connection/network concepts (that is the server runtime in
  `Database.Hosting`, and `Database.Client`).
- No DI or configuration surface (that is `Database.Hosting`'s seam alone).
- No model-specific request or result types — models subclass the
  `Database.Execution` family in their own packages.
