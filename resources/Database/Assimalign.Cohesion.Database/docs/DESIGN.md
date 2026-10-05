# Assimalign.Cohesion.Database — Design

The area root (architecture: [resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md)).
Everything here must be true for *all five* data models — anything model-specific
belongs in a model package. The root's job is to make engines substitutable at the
seams the platform builds on: the server serves any engine, the hosting layer
composes any engine, a client result looks the same regardless of the engine that
produced it.

The root is also the area's **rollup**: it references every child root — the
independently consumable base components a database is made of (`Database.Types`,
`Database.Language`, `Database.Storage`, `Database.Transactions`,
`Database.Execution`, `Database.Indexing`, `Database.Protocol`,
`Database.Security`) — so one reference to the root delivers the whole base
surface. Child roots never reference the root.


## Phase 29 composition contract (current)

The approved [hosting composition](../../../../docs/programs/DATABASE_HOSTING_DESIGN.md) supersedes the historical builder/worker descriptions below. `IDatabaseApplicationBuilder` exposes exactly borrowed `AddEngine(instance)`, owned `AddEngine(Func<IDatabaseApplicationContext, IDatabaseEngine>)`, and one-shot `Build()`. There is no builder engine enumeration, application-level AddServer, or Use stage. `IDatabaseApplication` inherits `IAsyncDisposable`; its context observes every engine, nested servers, and ordinal `GetEngine(name)` lookup. All four named engine operations use the existing `DatabaseName` value object; its implicit string conversions preserve straightforward callers while implementations use the typed contract.

`IDatabaseEngineBuilder` earns a shared seam because AddWorker is model-agnostic: the same factory can attach a worker to any model. Shared construction/rollback source is owned in this project's `shared/` and compiled by each model through `CohesionSharedSource`. Every model-specific interface extends the base and carries that model's options. No separate generic application composition algorithm consumes arbitrary model options. Strongly typed worker/server factory overloads are deliberately omitted: the common engine factory contract already works, and explicit casts for SQL/KeyValue servers avoid overload ambiguity and a second factory vocabulary.

Workers remain scheduled and quiesced by their engine. `IDatabaseEngineWorker.Run(CancellationToken)` exposes the existing executable pump so workers returned by the approved deferred factories can run without requiring a particular base implementation. `IDatabaseEngine.Servers` enables hosting to discover nested servers. Engines own factory-produced workers and servers; hosting snapshots servers for lifecycle only. Application-created engines are disposed by the application; instance-registered engines remain caller-owned.

```mermaid
classDiagram
    IDatabaseApplicationBuilder --> IDatabaseApplication : Build
    IDatabaseApplication --> IDatabaseApplicationContext : Context
    IDatabaseApplicationContext --> IDatabaseEngine : Engines
    IDatabaseEngineBuilder --> IDatabaseEngine : Build
    IDatabaseEngine --> IDatabaseServer : Servers
    IDatabaseEngine --> IDatabaseEngineWorker : Workers
```
## Why-this-not-that decisions

- **Child roots roll up under the root; they never reference it** (owner
  decision, 2026-07-13 — inverts the earlier Protocol→root and Transactions→root
  references). Unlike the Web area, a database has a vast base-component surface;
  the child roots exist to break it out for separation of concerns and
  testability, and each must stay *independently consumable* — a tool that only
  speaks the wire protocol, or a storage engine experiment, should not drag the
  area contracts in. That is only true if the dependency arrow points root →
  child. The rejected alternative — child roots referencing the root for shared
  vocabulary (`DatabaseException` ancestry, `TransactionId`, `ProtocolVersion`) —
  made two children (`Protocol`, `Transactions`) unaggregatable and forced the
  hosting module into a COHRES002 exemption for the server's own machinery. With
  the inversion, vocabulary lives with its owning child (`ProtocolVersion` in
  `Protocol`, `TransactionId`/`TransactionState` in `Transactions`), the root
  consumes it through its child-root references, and consumers reach it
  transitively through the root. `Database.Indexing` joined the child
  roots on 2026-07-13 (owner direction): its only root coupling was exception
  ancestry, re-rooted onto its own `IndexException` — index infrastructure is a
  base component like storage and transactions.

- **Engine → database → session → transaction as four contracts**, not one god
  interface. Each has a distinct lifetime and threading model: engines are
  process-long and thread-safe; databases are shared handles; sessions are
  cheap, single-threaded execution scopes; transactions are explicit ACID
  brackets inside a session. Collapsing them (an `ExecuteAsync` on the engine,
  say) would smuggle session state into a shared object.
- **Engines are data machines — create → use → dispose, no lifecycle members**
  (owner decision, 2026-07-13 — **reverses the #903 decision** that put
  `StartAsync`/`StopAsync` on the root engine contract, and supersedes the
  short-lived `IDatabaseEngineLifecycle` segregation from earlier the same day,
  which was deleted before it ever shipped in a release). The new information
  that changed the calculus: once servers became per-model (below), "running"
  had an owner — the server fronts the engine on the network and is the thing
  that starts and stops — and the engine's start/stop ceremony was revealed as
  accidental service-shape, not data-machine substance. An engine is fully
  operational from creation (its background workers spawn with it) and disposal
  is its one transition: quiesce workers → durable flush → close databases.
  What #903 actually needed — a host that can *align* engine durability with
  its own lifecycle — is satisfied by disposal alone: the composition root that
  created the engine disposes it, and committed work is durable when
  `DisposeAsync` completes. The rejected alternative (keeping idempotent
  start/stop for restartability) bought a restartable engine object nobody
  needed — a "restart" is creating a fresh engine over the same storage root,
  which the recovery path already makes correct — at the cost of a
  four-state machine on every engine and a start-order protocol between host
  and composition root.
- **A minimal observational `State` stays on the engine** (judgment call,
  recorded): the approved data-machine contract needs no state machine, but
  worker-fault reporting needs *somewhere* to surface — the old contract threw
  the recorded fault from `StopAsync`, and with stop gone the only alternatives
  were throwing from `DisposeAsync` (hostile to `await using`, masks in-flight
  exceptions) or silence. `EngineState` therefore shrank from a six-state
  lifecycle enum to three observational conditions: `Running` (from creation),
  `Faulted` (a background-worker fault was recorded; the engine keeps serving —
  grouped commits self-help, checkpoints just stop truncating — but the owner
  should learn it runs degraded), `Disposed`. The default control-plane health
  aggregate delivered by #973 reads this surface; nothing drives transitions
  from outside. Since #1268 `Faulted` means a worker *holds a failure it has not
  worked off*: the guided base keeps a failure record per database
  (`DatabaseEngineWorker.Fault`, `ConsecutiveFailures`, `FailureCount`), and a
  database's record ends with the first pass that finishes that database's work, so a
  transient fault does not leave the engine `Faulted` for good, and one database's
  deferred or busy work never keeps another database's resolved failure reported. Only
  a worker that implements `IDatabaseEngineWorker` without the base and lets its loop
  end early is recorded until disposal: the engine cannot tell when such a worker is
  healthy again (the hosting health description says so).
- **A worker failure never ends a worker, and one database's failure slows no other
  (#1268 and its review).** `DatabaseEngineWorker.Run` is a non-virtual loop over the
  non-virtual pass `RunIteration` and the protected `void RunIterationCore`. A pass
  visits the engine's databases one by one: it asks `BeginDatabase(name)` first, and
  catches and reports a database's failure (`ReportFailure(name, exception)`) before
  going on to the next database. A database whose failure was reported is skipped by
  later passes until `DatabaseEngineWorker.FailureBackoff` (one second) has passed,
  while every other database keeps the worker's full pace; work a pass leaves for later
  without failing (a busy storage, a checkpoint deferred to a running statement, an undo
  still deferred) is reported with `ReportUnfinished(name)`, which keeps an earlier
  failure of that database recorded until the work is done. A database a pass does not
  begin (dropped, closed, offline) has its record forgotten. Only a pass that fails as a
  whole (its work threw, or its trigger wait did) makes the loop sleep the backoff, so it
  cannot spin. The version-purge workers report an undo failure with no extra backoff
  (`ReportFailure(name, exception, TimeSpan.Zero)`): the coordinator already paces each
  retry, about 100 ms after the deferral and then doubling (#1226). Passes never
  overlap; a test that calls `RunIteration` beside the worker's thread waits for the
  running pass. Only cancellation and `OutOfMemoryException` leave the loop. The
  engines' pumps (one shared copy, `shared/DatabaseEngineWorkerPump.cs`, until the
  phase-3 engine base takes it over) also run a worker's `Run` again after the backoff
  if it ever throws or returns early. A worker's name, kind and cadence are fixed by its
  constructor since phase 3 (#1259): they used to be abstract getters, so the cadence of the
  built-in workers was read from the engine's options object on every trigger wait, and a change
  to that object after the engine was created changed it; now the value the engine was created
  with holds for the worker's life. This is PostgreSQL's recovery for its background
  writer, checkpointer and WAL writer, which catch an error per cycle, report it,
  release what the cycle held and sleep a second before the loop continues ("A write
  error is likely to be repeated", `src/backend/postmaster/bgwriter.c:154-205`,
  `checkpointer.c:286-346`, `walwriter.c:147-193`); Neo4j's checkpoint scheduler
  likewise counts consecutive failures and clears them on the next success
  (`community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:51-84`).
  A PostgreSQL checkpointer serves one cluster, so its sleep holds back no other
  database; a worker here serves every database of its engine, which is why the
  backoff is per database. Before the review the backoff was the worker's: while one
  database kept failing, every other database got at most one checkpoint a second (a
  probe measured 6 truncations in 6 s instead of 458, and a journal peak of 700-884
  times the size trigger).
  A failure the worker cannot recover from is not the worker's: a failed durable flush
  (#1243), drain of the journal's append buffer (#1252) or header slot write (#1268) takes
  the database offline, the workers skip it, and `OfflineDatabases` reports it. A drain
  fails on whichever thread needs the records on the file, a worker's group flush,
  checkpoint or page write-back included, and its cause is `StorageOfflineCause.JournalFlush`
  like the fsync's. Before #1268 the engines' pumps caught outside
  `Run`'s loop, so one unexpected exception (a page write the checkpoint could not
  make) ended a checkpoint, write-back or flush worker for the life of the engine.
- **A checkpoint that hangs or crawls holds back its own database only.** The per-database
  backoff above bounds how often a failing database is tried, not how long one try takes:
  the checkpoint worker used to run every database's checkpoint on its own thread, so a
  device that took seconds to answer an fsync, or to fail a write, stopped every other
  database's journal truncation for as long (a hung data-file fsync in one database left
  the other with no checkpoint at all in five seconds of writes). The engines' checkpointer
  is now one shared copy, `shared/DatabaseCheckpointWorker.cs`, that runs each database's
  checkpoint on a lane (`shared/DatabaseCheckpointLanes.cs`): a dedicated lane thread,
  at most one checkpoint per database at a time. The pass waits for the lane while no
  other database needs the worker, so a checkpoint that ends is settled by the pass that
  started it, as before. Once another database's journal reaches its size (the engine's
  checkpoint signal), or the poll interval passes, the pass leaves the checkpoint running
  alone; later passes skip that database, keeping a failure recorded for it, until the
  checkpoint ends, and the pass that finds it ended settles it. Engine disposal waits for
  a checkpoint left running before it closes the storages. This is the isolation Neo4j
  gets from one checkpoint job per database, each rescheduled only after its run ended
  (`community/kernel/src/main/java/org/neo4j/kernel/database/Database.java:1155-1157`,
  `community/kernel/src/main/java/org/neo4j/wal/checkpoint/CheckPointScheduler.java:51-84`),
  and PostgreSQL from one checkpointer per cluster and WAL
  (`src/backend/postmaster/checkpointer.c:5`). The page write-back, flush and purge
  workers still visit their databases on their own thread (follow-up).
- **A failure that never clears is retried forever (owner decision pending).** A page
  write the checkpoint can never make leaves its database's journal untruncated, so the
  journal grows until the fault clears or the disk fills, with the engine `Faulted` and
  the application `Degraded` meanwhile. That is PostgreSQL's behavior (its checkpointer
  keeps retrying); Neo4j instead panics the database after ten consecutive checkpoint
  failures (`CheckPointScheduler.java:38-42`, `67-75`). The escalation, if any (offline
  after N failures, or past a journal ceiling), is an owner decision recorded as a
  follow-up of #1268.
- **An offline database is reported beside the state, not in it** (#1243 review).
  A database whose fsync (#1243), drain of a journal's append buffer (#1252) or header slot
  write (#1268) failed refuses every request while its engine keeps
  serving the others, so `EngineState` does not change; `IDatabaseEngine.OfflineDatabases`
  lists the open databases that are offline, and the hosting health aggregate
  reports the application unhealthy while the list is not empty. Before this an
  offline database left health `Healthy`, so neither an operator nor an
  orchestrator acting on health learned of it.
- **The application exposes its composition through `IDatabaseApplicationContext`,
  and the context is plural** (owner direction, 2026-07-13 — the Database
  instance of the Web area's `IWebApplicationContext` pattern, converged with
  Web's shape). The context carries `Servers` (registration order — plural
  because servers are per-model, so one application may front SQL and Documents
  engines through two servers) and `Engines` (the server-less, embedded
  registrations; an engine fronted by a server is reachable through that
  server's context). `IDatabaseApplication` is the Web shape exactly: `Context`
  plus `StartAsync`/`StopAsync` (the loose `Engines` member it briefly carried
  is gone). Deferred composition callbacks on the builder receive the context —
  `AddServer(Func<IDatabaseApplicationContext, IDatabaseServer>)`, mirroring
  Web — replacing the earlier engine-list-receiving factory: the context view
  lets a factory observe servers registered ahead of it, not just engines, and
  keeps the callback signature stable as the context grows. An earlier cut of
  this contract (same day, superseded before merge) exposed a single nullable
  `Server`; per-model servers made plurality structural, not optional.
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
- **The isolation-level seam consumes the Transactions child's enum directly**
  (2026-07-13, with the MVCC-integration design — area DESIGN.md §3.8).
  `IDatabaseSession.BeginTransactionAsync(IsolationLevel, …)` and
  `IDatabaseTransaction.IsolationLevel` speak `Database.Transactions`'
  `IsolationLevel` — the same pattern as `TransactionId`/`TransactionState`:
  post-inversion, the root consumes child vocabulary rather than duplicating
  it. The rejected alternative — a root-owned isolation enum mapped onto the
  child's — would create two vocabularies for one concept and a translation
  layer with no owner. The contract deliberately allows engines to run
  *stronger* than requested (the SQL engine's page-grain serialization today),
  never weaker, so the seam could land ahead of the MVCC session binding
  without lying about semantics. No speculative MVCC contracts were added to
  the root: the manager's contracts already live in the Transactions child
  root, and the binding is a model-engine concern (the design doc explains the
  placement).
- **`DatabaseParseException` as a first-class error category.** Parse failures
  and execution failures have different wire error codes (`ParseFailure` vs
  `ExecutionFailure`) and different caller responses (fix the text vs inspect
  the data). A subtype of `DatabaseException` keeps existing `catch` blocks
  working while giving the server an exact mapping — better than string-matching
  messages or per-model exception knowledge in the server.
- **`QueryRequest`/`QueryResult` live in `Database.Execution`, not here.** The
  root aggregates the execution family rather than owning it: execution is its
  own child root with pipeline/context machinery the contract root has no
  business carrying. The same holds for the other child-root vocabularies the
  root's contracts speak: `TransactionId`/`TransactionState` live in
  `Database.Transactions` (`IDatabaseTransaction` consumes them), and
  `ProtocolVersion` lives in `Database.Protocol` (`IDatabaseServerSession`
  consumes it).
- **Background workers are engine-owned, unconditionally — the claim handshake
  is gone** (owner decision, 2026-07-13; supersedes the #902 claim model). The
  engine spawns its worker loops at creation — the latency-critical flusher and
  write-back loops on dedicated threads the engine itself owns, satisfying the
  Lane-H dedicated-thread guardrail with no host involvement — and quiesces
  them on dispose. `IDatabaseEngineWorker` shrank to an **observational**
  contract (name, kind, cadence — what a diagnostics or health surface needs);
  the pump machinery (`Run`/`RunIteration`/`WaitForTrigger`) lives on the
  guided base `DatabaseEngineWorker` for the owning engine's internal use only.
  The rejected (previous) design — `TryClaim`/`Release` plus host worker slots
  mapping claimed workers onto the hosting execution menu — existed to let a
  host own worker scheduling; per R10 the engine had to own the *work* anyway,
  so the handshake bought configurability nobody used at the price of a
  two-owner protocol whose failure modes (claim races, disabled slots,
  half-claimed inventories) all had to be designed away. One owner, no
  handshake: a worker can never run twice because exactly one engine-internal
  scheduler exists. Workers remain synchronous by design — every body is
  storage I/O (fsync, page writes, checkpoint), which has no async fast path.
- **Server *contracts* live here — and they are the only area-wide server
  requirement; servers are per-model, each model package carrying its own copy
  of the server machinery** (owner decision 2026-07-14, settled on review of
  the second model server's extraction evidence). A server fronts exactly
  **one** engine — `IDatabaseServerContext.Engine` is singular — so
  model-specific wire behavior has a home. Each model ships its own
  `IDatabaseServer`/`IDatabaseServerContext`/`IDatabaseServerSession`
  implementation in its model package (`SqlDatabaseServer` in `Database.Sql`,
  `KeyValueDatabaseServer` in `Database.KeyValuePair`), with the machinery
  (accept loop, session state machine and frame pump, guardrails, two-phase
  drain) internal to that package. The placement history is deliberate
  evidence discipline: the shared base that briefly existed at n=1 was folded
  into `Database.Sql` with an extraction trigger recorded; the second model
  server fired it and the proven core was extracted into a shared
  `Database.Server` — and the owner then reviewed that evidence and chose
  per-model duplication anyway (model independence over shared code; drift
  cost accepted; wire parity held by the protocol contract and per-model E2Es —
  the preserved prediction-vs-evidence table lives in the area DESIGN §3.10).
  The contracts stay here for the same COHRES001 reason as before: feature
  libraries (quotas #167, health, `Database.Testing`) must be
  able to name the server without referencing any runtime. The context shape
  (`Context` = engine + sessions) mirrors the application context pattern —
  observational composition on a context, lifecycle on the owning object.
- **The application builder is a root seam; the implementation is not** (owner
  direction, 2026-07-13). `IDatabaseApplicationBuilder`/`IDatabaseApplication`
  live here so **model packages register their engines and servers without
  knowing the hosting implementation**: `Database.Sql` ships `AddSqlDatabase(...)` and
  `AddSqlServer(...)` as `extension(IDatabaseApplicationBuilder)` members and
  never references `Database.Hosting` (COHRES001 intact); the hosting module
  ships the implementation (`DatabaseApplicationBuilder`) and the creation
  entry point (`DatabaseApplication.CreateBuilder()`). The concrete
  `DatabaseApplicationBuilder.AddService` in `Database.Hosting` accepts plain Hosting
  service instances and context factories; those services start before servers and
  stop after them in reverse order. The root references no hosting library and
  exposes no service-registration verb (O34). Multiple `AddServer` registrations are
  allowed — servers are per-model. This mirrors the Web area exactly
  (`IWebApplicationBuilder` in the `Web` root, `WebApplication.CreateBuilder()`
  in `Web.Hosting`, `AddAuthentication` in `Web.Authentication`) — and the
  pattern is the **cross-area expectation**: every area root provides
  `I<Area>ApplicationBuilder`, and feature/model registration verbs ship with
  their feature package (see `.claude/rules/resource-areas.md`). The rejected
  alternative — a builder type in the hosting module — would force every model
  package that wants a registration verb to reference the composition surface,
  which is precisely what the hosting-isolation rule forbids.
- **Model-specific schemas belong to their model family** (MVP features A1–A4,
  2026-09-17). `CompiledSchema` is an abstract model-agnostic identity carrying
  `Format`, `Name`, `Model`, `AllowsDestructiveChanges`, and the model's canonical
  document. The root computes SHA-256 over that document without inspecting its
  shape. `IDatabaseSchemaProvisioner` remains the common apply seam, returning
  the readonly `SchemaMigrationResult` value (`FromHash`, `ToHash`,
  `OperationCount`, `WasAlreadyApplied`). Relational declarations, builders,
  validation, serialization, and migration planning moved to the thin
  `Database.Sql.Schema` package, which build tooling can consume without the SQL
  engine or network stack. The former root schema's collection shape was removed;
  the document model will define its own vocabulary in its own model family.
  If another model needs a different shape, it does not belong in this root.
- **Object ownership separates code-first provisioning from ad-hoc statements.**
  `DatabaseObjectOwner.Adhoc` objects remain fully mutable through session
  statements. `DatabaseObjectOwner.Schema` objects can change only through schema
  apply; a session attempting to alter or drop one receives
  `DatabaseObjectLockedException` identifying the object, its compiled
  `OwningSchema`, and the operation. `OwningSchema` is provisioning identity,
  distinct from any model-specific namespace such as a SQL table's `Schema`.
  Model catalogs persist ownership and model engines enforce it. Neither the
  ownership contract nor the exception requires a relational object shape.
- **`ProtocolVersion` lives in `Database.Protocol`, and the root consumes it.**
  The struct is wire vocabulary, so it lives with the wire implementation —
  `ProtocolVersion.Current` ("the version this assembly implements") is a plain
  static property on the struct, with the claim and the implementation that
  makes it true in one assembly. The struct spent a period in the root with
  `Current` grafted on from `Protocol` as a C# 14 static extension member — an
  arrangement that existed *only* because `Protocol` referenced the root, which
  made root → `Protocol` impossible. The child-root inversion removed that
  constraint, so the split was collapsed back into the protocol package.

## Error model

`DatabaseException` (inherits `Exception` per the area-scoped exception rule) is
the root for **the contract root and everything built *above* it**: the model
engines and their satellites (`SqlCatalogException`, engine-thrown
`DatabaseException`s), the client core (`DatabaseClientException`,
`SqlClientException`), the server, and `Database.Embedded`.
The root defines semantic subtypes, each because the distinction is part
of a public contract: `DatabaseNotFoundException` is the exact absence signal
from `IDatabaseEngine.OpenDatabaseAsync` (so provisioning and server binding do
not confuse an operational failure with a missing database),
`DatabaseParseException` distinguishes fix-the-text from fix-the-data failures
(the wire's `ParseFailure`), and the retryable-abort pair
`DatabaseTransactionAbortedException` / `DatabaseTransactionDeadlockException`
(the model-boundary surface of the transaction kernel's aborts: a write-write
conflict or deadlock victim is retryable by construction, and in-process
consumers deserve to catch that kind precisely rather than parse messages; on
the wire both remain `ExecutionFailure` with a precise message).
`DatabaseTransactionCommitUnconfirmedException` is the non-retryable outcome of a
commit whose record was written but whose fsync failed: the work may have committed,
so retrying it could apply it twice. `DatabaseOfflineException` (#1243) is what every
operation gets after such a failure, or after any failed fsync of a database's journal
or data files: the storage stopped writing (the storage's `StorageOfflineException`,
`COHDBS002`, is its inner exception), and the database refuses everything until
`IDatabaseEngine.OpenDatabaseAsync` reopens it and recovery decides the unconfirmed
commit — PostgreSQL's `PANIC` on a failed WAL fsync, scoped to one database instead of
the process. Its `Code` leads the message and names the model: `COHSQLT004`,
`COHDBK002`, `COHDBD002`, `COHDBG012`, `COHDBB002`. Every wire server reports it as
`Unavailable`. `DatabaseOfflineException.Create` builds it from the storage error, and its
message names what failed from the storage's typed `StorageOfflineException.Cause`
(`JournalFlush`, `DataFlush`, `HeaderWrite`; the root words each cause itself, so callers
tell the causes apart by the enum, never by the text). An
operation that committed by itself (a self-committing statement such as SQL DDL, or any
storage bracket whose commit record was written before the flush failed,
`StorageOfflineException.CommitRecordWritten`) is never reported as refused, because its
work can survive the reopen: `DatabaseTransactionCommitUnconfirmedException.Create` builds the
code-led unconfirmed error for it.

**Child roots own independent exception roots** — `StorageException`,
`DatabaseTypeException`, `ProtocolException`,
`TransactionAbortedException` all inherit `Exception` directly. This is the
point of the child-root inversion: a child root must be independently
consumable, so its error surface cannot depend on the area contracts. (Storage
and Types were always shaped this way; Protocol and Transactions joined them when
their root references were inverted, 2026-07-13. Execution has no exception root of its
own since its unused pipeline was deleted, #1257.)

The consequence, deliberately accepted: `catch (DatabaseException)` does **not**
catch child-root failures. The layer that owns both vocabularies translates at
its boundary — the server session pump maps `ProtocolException` to
`ProtocolViolation` in a dedicated handler and the client core wraps it in
`DatabaseClientException`. A model engine that surfaces a child-root failure
(storage conflict, transaction abort) through the session contract is
responsible for wrapping it in a `DatabaseException` at the model boundary; a
child-root exception that escapes raw reaches the wire as the `Internal` error
(and closes the session), which is the pre-existing behavior for
`StorageException` — the SQL engine's statement-level failures already surface
as `DatabaseException`, so the inversion changed no live wire mapping.

## Lifecycle pattern

- Engines: **no lifecycle members** (the data-machine decision above). An engine
  is operational from creation — background workers pumping, databases
  creatable — and disposal (`IAsyncDisposable` + `IDisposable`, idempotent) is
  its one transition: quiesce workers → durable flush → close every open
  database. Committed work is durable when `DisposeAsync` completes. `State` is
  observational only (`Running`/`Faulted`/`Disposed`).
- Servers: `StartAsync`/`StopAsync` on `IDatabaseServer` — "running" lives on
  the per-model server (and the application composing servers), never on the
  engine. Stop drains gracefully within the server's drain budget; disposal
  stops the server.
- Sessions: disposing rolls back any active transaction (documented on the
  interface; sessions must never commit implicitly on dispose).
- Transactions: disposing an uncommitted transaction rolls it back.

## Diagnostics

The root raises its own events through one internal event source, named for the assembly:
`Assimalign.Cohesion.Database` (`src/Internal/EventSource/DatabaseEventSource.cs`, #1268
review). Every engine model's workers derive from `DatabaseEngineWorker`, which writes these
events, so the one source covers the workers of all five engines. A worker's failure is retried,
so it is a `Warning`; a failure that repeats is written at most once per `FailureBackoff` per
database (once per coordinator retry for a deferred undo), and the recovery that ends it is
`Informational`. PostgreSQL reports every error of a background worker's cycle the same way
before it sleeps and retries (`src/backend/postmaster/checkpointer.c:294-295`).

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | `WorkerFailed` | Warning | `workerName`, `workerKind`, `database` (empty for a failure of the whole pass or of its trigger wait), `exceptionType` (full name), `exceptionMessage`, `consecutiveFailures` |
| 2 | `WorkerRecovered` | Informational | `workerName`, `workerKind`, `database` (empty for the worker's passes), `failures` |

No counters: a worker's counts are on the worker (`FailureCount`, `ConsecutiveFailures`), and
the hosting health aggregate reports them. The health output names a failing worker and the
type of its failure only, because the health endpoint is unauthenticated and an exception's
message can carry file paths; the event carries the message. A database going offline is not a
worker event: the engines report it through `IDatabaseEngine.OfflineDatabases` and health, and
every refusal carries the failure that took it offline (`DatabaseOfflineException`'s inner
`StorageOfflineException`, its `Cause` and its I/O error). This source does not write the
transition either, and cannot without breaking the event-source convention: an engine model sees
it in its storage's `OnOffline` hook, outside the root, and the root may expose no public entry
point into this internal source (`.claude/rules/event-source.md`, rule 2). An offline event
therefore belongs in each engine model's own source, which none has yet: an open follow-up of
the #1268 review. Until then a database a drain on a worker's thread took offline (#1252) is
visible as it happens only in health, and its cause in the next refused operation.

## AOT posture

Contracts, enums, value objects, and canonical-document hashing only. The root does not inspect
model schemas or discover types dynamically. The event source writes only strings and integers,
which bind to the trim-safe `WriteEvent` overloads; a NativeAOT application receives its events
only with `<EventSourceSupport>true</EventSourceSupport>`, and nothing depends on delivery. SQL declaration compilation and source-generated
JSON serialization live in `Database.Sql.Schema`.

## Non-goals

- No connection/network concepts (that is the per-model server machinery in
  the model packages — `SqlDatabaseServer` in `Database.Sql` — and
  `Database.Client`).
- No DI, configuration, or `Assimalign.Cohesion.Hosting*` reference. Background-work
  registration and hosting implementation remain in `Database.Hosting` (O34).
- No model-specific request or result types — models subclass the
  `Database.Execution` family in their own packages.
