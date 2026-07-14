# Assimalign.Cohesion.Database.Sql — Design

The SQL engine (area architecture: [resources/Database/DESIGN.md](../../DESIGN.md)
§3.3): parse (`Sql.Language`) → plan (`SqlPlanner`) → execute (`SqlPlanExecutor`)
against shared storage, with the catalog (`Sql.Catalog`) as schema authority.

## Execution model

- **Rule-based planning, plan/execute split.** `SqlPlanner` binds the AST against
  the catalog into a small `SqlPlan` IR and rejects everything outside the
  executor's surface *at plan time with precise messages* (JOIN, GROUP BY,
  subqueries, aggregates beyond a lone `COUNT(*)`, `INSERT ... SELECT`). The IR
  is deliberately thin — a cost-based planner replaces the binding internals
  later without changing the executor seam (#178's plan-stage requirement, MVP
  shape).
- **Row format: MVCC stamps + object-id-prefixed tuple, in per-object page
  chains (record-space format version 3).** Every data record is
  `[writer u64][deleter u64]` — a fixed 16-byte version-stamp header, the
  B+Tree leaf-entry design adopted for the record space — followed by the
  shared tuple codec payload (#854): the owning table's object id, then one
  self-describing component per column. Why a fixed binary prefix and not
  tuple components (the rejected alternative): (a) stamps in front never
  disturb ADD COLUMN's O(1) null-tail decode, which depends on missing
  components being *trailing*; (b) fixed width makes tombstoning a same-length
  in-place write — a delete can never relocate a record; (c) stamp reads don't
  pay tuple-decode costs on the scan hot path. Since format version 3 the
  tables of a database still share one record *space* but not one page
  stream: rows land on pages tagged with their table's object id (the storage
  layer's per-owner chains), so **a table scan touches only its own table's
  pages** — O(table), not O(database) — and `DROP TABLE` releases the
  table's whole chain back to the allocator (transactionally, inside the
  statement bracket; the record-byte layout is unchanged from version 2, and
  the object-id prefix stays as defense in depth and upgrade detection).
- **Scans are snapshot-visible.** Every scan filters through the statement's
  snapshot: a version is visible when `IsVisible(writer)` and its deleter — when
  stamped — is *not* admitted (a visible tombstone reads as absence). Updates
  write version chains in the record space itself: tombstone the old version in
  place, insert the new one, both stamped with the writing transaction's
  sequence — so exactly one version of a logical row is visible per snapshot by
  construction, versions are WAL-covered like all record writes (restart keeps
  them correct for free), and aborted stamps revert physically with the page
  images. Deletes tombstone (older snapshots keep the row until the purge
  worker reclaims below every live horizon). DDL row rewrites (DROP COLUMN)
  walk *every* stored version, visible or not, preserving stamps.
- **Migration rule (record-space format version, catalog-persisted).** The
  catalog stores the record-space format version (kind-4 record): 1 = the
  pre-MVCC unstamped layout, 2 = stamped rows in the shared page stream, 3 =
  stamped rows in per-object page chains. Older databases upgrade in place at
  open, stage by stage, marker written after both stages so each is
  idempotent across the two-storage crash window: (1 → 2) every record gains
  a zeroed stamp header (writer 0 = committed bootstrap data, visible to
  every snapshot) under one storage transaction — idempotent because a
  version-1 record always begins with the tuple codec's nonzero Int64 tag
  byte, so an already-stamped record is provably upgraded and skipped on
  replay; (2 → 3) rows relocate from the shared (owner-zero) pages into their
  table's chain, stamps preserved verbatim (visibility unchanged), the
  emptied shared pages released, and rows whose object id no longer exists in
  the catalog (residue of pre-chain DROP TABLEs) dropped rather than moved —
  idempotent because the stage reads only owner-zero pages and a moved record
  lives on an owner-tagged page. Relocation changes row locations, which is
  safe at upgrade time: nothing persistent references locations (the
  version-store ledger dies with the process; index entries reference
  locations only from format 3 onward, and a version-2 database cannot have
  SQL indexes).
- **Schema evolution:** `ADD COLUMN` is O(1) — missing trailing components decode
  as null; `DROP COLUMN` rewrites the table's rows (positional records), inside
  the caller's transaction.
- **Expression evaluation** is interpretive with SQL null propagation (nulls
  reject predicates, comparisons with null are null, `AND`/`OR` are three-valued),
  numeric promotion to decimal, ordinal string comparison, hand-rolled `LIKE`
  (`%`/`_`), `CASE`, `BETWEEN`, `IN` (lists), `IS NULL`, parameters (`@name`
  bound by bare name), and a small builtin set (`COALESCE`, `UPPER`, `LOWER`,
  `LENGTH`, `ABS`). Compiled expression plans are a later optimization.
- **SELECT materializes.** Sorting and `DISTINCT` need the full result anyway at
  this stage; `SqlMaterializedResultSet` carries typed columns and evaluated
  rows. Streaming operators arrive with the planner build-out.
- **Transactions (MVCC session binding, §3.8).** Every statement — explicit
  transaction or auto-commit — runs under an `ITransactionContext` from the
  database's transaction manager, whose sequences come from the storage's own
  counter (one namespace). The per-database `SqlTransactionCoordinator` owns
  the composition (manager + lock manager + record-space version store +
  journal-bound log) and implements `IStorageTransactionSource` — the pairing
  seam now resolves a context's *current statement bracket*. Commit flows
  through the manager: its journal-bound log appends the commit record and
  awaits durability (which, by journal ordering, also covers every statement
  bracket the transaction committed non-durably); rollback is **logical** —
  the version store's ledger physically undoes the writer's stamps before its
  locks release. Auto-commit statements ride a one-statement manager
  transaction, so visibility semantics never fork. Isolation: `Snapshot`
  (default) fixes the statement snapshot at begin, `ReadCommitted` re-captures
  per statement; `Serializable` is **rejected** until conflict detection
  exists (never run weaker than requested). Kernel aborts surface wrapped in
  the root's `DatabaseTransactionAbortedException` (deadlock victims:
  `DatabaseTransactionDeadlockException` — retryable by construction, an
  `ExecutionFailure` on the wire, session stays usable). On every database
  open the coordinator runs `TransactionRecovery.Analyze` over the recovered
  journal (the storage strategy defers the open-time checkpoint for exactly
  this) and scrubs every unproven writer's stamps out of the record space —
  the open-time bulk form of `IVersionStore.PurgeWriterAsync`, one pass
  instead of one scan per writer because the in-memory ledger died with the
  process; the checkpoint worker checkpoints data storages *through the
  coordinator*, so truncating checkpoint records carry in-flight logical
  sequences. DDL flows to the catalog, which self-commits on its own storage
  (see the catalog DESIGN.md for why DDL-in-DML is out of MVP scope), and
  interlocks with row writers through table-grain intent locks (below).
- **Write statements execute in two phases; the physical bracket is per
  statement (§3.8's migration path).** Phase one — no physical bracket: scan
  through the statement snapshot, collect targets, acquire an IntentExclusive
  table lock and an Exclusive lock per target row through the lock manager
  (asynchronous waits, cancellation-honoring; deadlocks detected here,
  requester-closes-cycle). **Lock-key scheme:** row locks key on
  `LockResource.Entry(objectId, packed page/slot location)` — the same packed
  identity the version-store ledger uses, and the same `LockResource` space
  the B+Tree's hashed key locks live in, so index and row locks cannot alias.
  Phase two — the coordinator's **apply gate** (one writer statement applies
  at a time per database): open the statement's storage bracket, **re-validate
  every target against its current stamps** (the latest-state check under the
  exclusive lock — the B+Tree uniqueness-discipline precedent; a snapshot-only
  check would admit write skew), apply, and commit the bracket *non-durably*
  (the transaction's commit record owns durability through journal ordering; a
  statement-level failure still rolls the bracket back physically). A target
  tombstoned by a concurrently *committed* transaction fails the statement
  with the retryable conflict — **first-updater-wins**; under `ReadCommitted`
  this is deliberately stricter than PostgreSQL's re-evaluation (the statement
  aborts rather than re-targeting the new version — retry is the policy).
  **Why the apply gate and not concurrent appliers with page-conflict retry
  (the recorded page-conflict fallback decision):** page locks release at
  statement end either way, so the gate costs only intra-database physical
  apply parallelism — which page-grain single-writer never had — while
  concurrent appliers reintroduce unbounded retry loops and page-vs-row wait
  cycles the lock manager cannot see. Revisited when per-object page chains
  landed (format version 3): chains remove data-page conflicts *between
  tables*, but writer statements still share the current write page within a
  table, the free-space map, and journal append ordering — the gate stays,
  and a per-object relaxation remains a measured-need follow-up, not a
  default. SELECT statements take no locks and no bracket: readers never block
  writers, and physical read/write interleaving is unchanged from the
  page-grain engine (a known storage-layer constraint, not widened by this
  design).
- **Two file sets per database:** `<name>` (data) and `<name>.catalog` — both via
  the engine's storage strategy, so file-backed and in-memory composition stays
  symmetric.

## Engine-owned background workers

The engine is a **data machine**: `Create(options)` returns it operational, with
the five-worker inventory already pumping — one dedicated background thread per
worker, spawned by the constructor and joined on dispose. Nothing outside the
engine schedules, claims, or configures these loops (the 2026-07-13 redesign
deleted the #902 claim handshake — see the root DESIGN.md); the root contract's
`IDatabaseEngineWorker` view of them is observational (name, kind, cadence).
Each worker iterates a lock-free snapshot of every open storage file set (data +
catalog per database, rebuilt when databases open/close; passes tolerate racing
a drop):

- **`SqlWriteAheadFlushWorker`** — signal-driven group-commit flusher. Every open
  storage's `OnCommitPending` hook sets one engine-level event; a pass resets the
  event first (a mid-pass registration re-arms it) then calls
  `FlushPendingCommits()` per storage. Only does work under
  `SqlDatabaseEngineOptions.Durability = Grouped`; the default stays synchronous
  per-commit fsync.
- **`SqlPageWriteBackWorker`** — paced `WriteBackDirtyPages(batch)` per storage per
  pass (`PageWriteBackInterval`/`PageWriteBackBatchSize`).
- **`SqlCheckpointWorker`** — checkpoints **both** file sets of every open database
  per pass (`CheckpointInterval`); a busy storage (`StorageTransactionException`) is
  skipped and retried next pass. The data file set checkpoints **through the
  database's transaction coordinator**, so the truncating checkpoint record
  carries every in-flight logical transaction's sequence (recovery
  classification survives truncation); the catalog file set has no logical
  transactions above it and checkpoints directly.
- **`SqlVersionPurgeWorker`** — **live** (#910): per pass, per open database, it
  retries the logical undo of any aborted writer whose rollback-time purge
  failed (`IVersionStore.PurgeWriterAsync`) and physically reclaims versions no
  snapshot can reach (`IVersionStore.PruneAsync` below the safe prune bound —
  the minimum snapshot floor of every open transaction, anchored above the
  recovered sequence namespace after a reopen, or the manager's oldest-active
  bound when idle; the manager's bound alone would let a live pinned snapshot
  lose a version it can still read). Cadence: `MaintenanceInterval`. A pass
  failure flips the engine to `Faulted` without stopping service — unpurged
  versions cost space, never consistency. The stub-era seam was untouched, as
  designed: activation changed the worker body and nothing else.
- **`SqlIndexMaintenanceWorker`** — the one remaining documented stub
  (sanctioned by #902): the index layer has no compaction to drive yet. It
  keeps the inventory — and any observer over it — stable; the throttled
  maintenance body fills in when index compaction lands, without touching the
  seam.

**Lifecycle (create → use → dispose):** the worker threads live exactly as long
as the engine. Disposal signals the pumps, joins the threads *before* closing
storages (no worker pass may touch a database being disposed), then durably
flushes and closes every open database — an embedded consumer gets identical
durability with no host and no composition at all (R10). A worker fault flips
the engine's observational `State` to `Faulted` without stopping service — a
faulted worker never compromises correctness (grouped commits self-help within
the window; checkpoints simply stop truncating), but the owner can observe the
engine runs degraded. (Previously the fault was thrown from `StopAsync`; with
lifecycle members gone, `State` is the reporting surface — throwing from
`DisposeAsync` would be hostile to `await using`.)

Cadence knobs live here, on `SqlDatabaseEngineOptions` — the engine owns the
loop, so cadence is engine configuration; observers read it through
`IDatabaseEngineWorker.Interval`.

## The SQL server runtime (`SqlDatabaseServer`)

The SQL model ships its own wire-protocol server: `SqlDatabaseServer`, a sealed
implementation of the area root's `IDatabaseServer` contract fronting exactly
one `SqlDatabaseEngine` (`Create(engine, options)`, options in
`SqlDatabaseServerOptions`). Servers are per-model by design: this type is
where SQL-specific wire behavior grows (typed relational payloads, SQL
transaction frames) as the protocol's model-specific surface lands; today
execution rides the model-agnostic text-execute seam on the root's
`IDatabaseSession`. "Running" lives here — the engine underneath has no
lifecycle; the server starts and stops around it.

### Why the machinery is Sql-internal (2026-07-14, owner decision)

The server machinery — accept loop, session state machine and frame pump,
guardrails, two-phase drain — lives **inside this package** (`Server/` for the
public surface, `Internal/` for the pump and context), not in a shared library.
The shared `Assimalign.Cohesion.Database.Server` base this server briefly
derived from was **premature abstraction from n=1**: only one model server
existed, and the future model servers are expected to diverge from the
SQL-shaped execute pump (Blob wants streaming, not header/row/complete framing;
KV wants binary command paths, not statement text). The root contracts
(`IDatabaseServer`/`IDatabaseServerContext`/`IDatabaseServerSession`) are the
**only area-wide requirement** — every model implements them its own way
against `Connections` and the `Database.Protocol` child root (via the root's
rollup). With no external derivers by design, the guided abstract base lost its
reason to exist and was merged into the sealed `SqlDatabaseServer` rather than
kept as an internal base: an internal abstract class with exactly one
same-assembly deriver is ceremony.

**Extraction trigger (recorded intent):** when the **second** model server is
built, extract the then-*proven* common core — predicted: the session table,
the guardrails (session limit / authentication timeout / idle eviction), and
the two-phase drain, **not** the execute pump — into a shared library, with the
second implementation as evidence of what is actually common. Model #2's
implementer extracts instead of copy-pasting.

### Composition seam

`SqlDatabaseServer.Create(engine, options)` — or the `AddSqlServer(engine,
configure)` builder verb — composes a server. The options carry a **bound
`IConnectionListener` instance**, not a listener factory: Connections drivers
bind at construction, so a factory would add a layer that defers nothing, and
passing the instance keeps ownership unambiguous — *the composition root
creates and disposes the listener; the server only accepts from it*. Stop is
signaled by cancelling the pending accept, never by disposing the listener.
`options.Authenticator` defaults to `DatabaseAuthenticator.AllowAll`
(`Database.Security`) — the MVP development posture, deliberately an explicit,
discoverable object rather than hidden server behavior. The engine is likewise
owned by the composition root: engines are data machines (create → use →
dispose), and the server never disposes its engine.

### The text-execute path

The server receives statement *text* and tuple-codec parameter bytes off the
wire; the bridge to the engine is the **text-execute seam on the root
contract** —
`IDatabaseSession.ExecuteAsync(string, IReadOnlyDictionary<string, object?>?, CancellationToken)`
— which `SqlDatabaseSession` implements with the model's own parser
(`SqlQueryRequest.FromSql`). Parameters decode with `DatabaseValueCodec`
(`Database.Types`), one self-describing component per parameter; result rows
encode the same way, one component per column, so both directions ride the one
shared codec the client also speaks.

### Session state machine

`Connected → Startup received → Authenticating → Ready ⇄ Executing → Terminated`.
Guardrails baked into the options because they are DoS-critical (the HTTP/1.1
limits lesson, #791): unauthenticated connections are dropped after
`AuthenticationTimeout`; `MaxSessions` bounds concurrency (rejections use the
protocol `Unavailable` error); idle sessions are evicted; `StopAsync` drains
within `ShutdownDrainTimeout` then aborts.

Implementation decisions (carried over from the server's prior homes — this
record moves with the machinery):

- **Version negotiation:** an unknown *major* in `Startup` earns
  `UnsupportedVersion` and a close; the server then speaks
  `ProtocolVersion.Current` (minors are additive by the protocol's contract, so
  no per-minor branching yet).
- **Database binding** resolves on the server's one engine: already-open
  databases first (`TryGetDatabase`), then an open attempt; no match →
  `DatabaseNotFound` and close. (The pre-per-model server probed a *list* of
  engines in registration order; one engine per server removed that ambiguity.)
- **Authenticate exchange (MVP):** the challenge frame carries no payload (the
  trust method); the client's response bytes pass to `IDatabaseAuthenticator`
  as opaque evidence. Method-specific payload schemas arrive with real
  authenticators.
- **`MaxSessions` counts handshaking sessions too** — an unauthenticated
  connection holds a slot, otherwise the cap would not bound resource use at
  all. Over-limit connections get the `Unavailable` error frame immediately at
  accept and never become sessions.
- **Error taxonomy per exchange:** statement-level failures keep the session in
  Ready — `DatabaseParseException` → `ParseFailure`, any other
  `DatabaseException` → `ExecutionFailure` (an execution error is not a protocol
  violation). Framing/order violations (`ProtocolException`, malformed parameter
  components) → `ProtocolViolation` **and close**; anything unexpected →
  `Internal` and close. A child-root exception that escapes raw (for example a
  `StorageException` the engine failed to wrap) reaches the wire as `Internal`
  and closes the session — the engine's model boundary is where wrapping into
  `DatabaseException` belongs.
- **Two-phase stop:** a *soft stop* token ends the accept loop and cancels reads
  at frame boundaries (idle sessions close immediately, telling the peer
  `Unavailable`); in-flight executions run on the session lifetime token and get
  the full drain budget. When the budget lapses, the *hard abort* token cancels
  executions and aborts connections. Session pumps own their errors — their
  completion tasks never fault, so drain is a plain `WhenAll`.

The server layer defines no exception root of its own: wire failures are the
protocol's (`ProtocolException`, mapped to wire error codes as above), and
engine failures are the area root's (`DatabaseException` family). Configuration
misuse (no listener, non-positive session limit, null engine) throws argument
exceptions at creation.

Server non-goals: no host-service adapter (`Database.Hosting` wraps
`IDatabaseServer` generically through the root seam); no connection-level
replication endpoints; no transaction frames yet (explicit transaction control
over the wire lands with the protocol's `Transaction` payload schema); no
TLS/transport policy — transports come bound from `libraries/Connections`
drivers, and the composition root owns them.

## The application-builder verbs (`AddSqlDatabase`, `AddSqlServer`)

The model registers itself on a database application through two
`extension(IDatabaseApplicationBuilder)` members in `Extensions/`, composing
against the **area root's builder seam only** (this package references no
hosting module; COHRES001 stays intact — the same rule that puts
`AddAuthentication` in `Web.Authentication`, not `Web.Hosting`):

- `AddSqlDatabase(Action<SqlDatabaseEngineOptions>?)` creates and registers the
  engine — operational the moment the verb returns — and returns it (the Web
  convention of returning the feature's own composition object), so a
  composition root can seed or provision databases, or front the engine with a
  server, before the application starts.
- `AddSqlServer(SqlDatabaseEngine, Action<SqlDatabaseServerOptions>)` creates and
  registers a `SqlDatabaseServer` fronting the given engine and returns it. The
  verb composes eagerly — a per-model server needs only its one engine, already
  in hand, so the deferred context-receiving `AddServer` overload exists for
  composition roots with genuinely late-bound decisions, not for model verbs.

Registration is dependency-free: the verbs new the objects up from options and
hand them to `AddEngine`/`AddServer`; no container, no configuration binding.

## Error model

`DatabaseException` (area root) for everything user-facing: plan-time
validation, execution errors, constraint violations (nullability). Parse failures
(`SqlQueryRequest.FromSql`, and therefore the session's text-execute seam) throw
the root's `DatabaseParseException` so callers — the wire-protocol server in
particular — can distinguish fix-the-text errors (`ParseFailure` on the wire)
from execution errors without model knowledge. `SqlCatalogException` (a `DatabaseException`) surfaces
catalog violations unchanged.

## The MVCC integration (scoped under #862)

The engine is the first adopter of the area's transaction-integration design
(`resources/Database/DESIGN.md` §3.8), which closes the isolation split-brain in
four independently shippable steps:

1. **Binding — delivered (#907):** `SqlDatabaseSession` begins an
   `ITransactionContext` on the database's transaction manager alongside the
   storage bracket (one shared sequence), paired through the coordinator's
   `IStorageTransactionSource`; commit/rollback flow through the manager
   (journal-bound log), the storage transaction stays the physical WAL bracket.
   The carried `IsolationLevel` is real per-level snapshot semantics — see
   "Transactions" under the execution model. **Scope decision:** the MVCC
   composition is per **database**, not per engine — the journal binding,
   recovery analysis, and the `OldestActive` prune bound are properties of one
   database's journal and record space; a per-engine manager would couple
   unrelated databases' snapshot horizons.
2. **Row versions + visibility — delivered (#908):** rows in the shared record
   space carry writer/deleter `TransactionSequence` stamps (the B+Tree
   leaf-entry design — tombstone deletes, aborted stamps reverting via page
   images); scans filter through `TransactionSnapshot.IsVisible`, closing the
   dirty-read window. **Version-layout decision:** chains live *in the record
   space itself* (update = tombstone old + insert new) rather than copying old
   versions out to a side store — in-space versions are WAL-covered, so restart
   visibility is correct by construction, no stable row identity is needed
   (the rejected copy-out design required one to key chains across record
   relocation), and the purge worker reclaims dead versions where they lie.
   See "Row format" and "Migration rule" under the execution model.
3. **Row-grain write conflicts — delivered (#909):** exclusive row locks via
   `ILockManager` (the B+Tree uniqueness-lock precedent) replaced page
   conflicts as the user-visible surface — concurrent writers to disjoint rows
   of one table (and one page) both commit; same-row writers wait, then
   resolve first-updater-wins; deadlock victims surface as the root's
   retryable `DatabaseTransactionDeadlockException`; DDL interlocks with row
   writers via table-grain intent locks. See "Write statements execute in two
   phases" under the execution model for the bracket/gate mechanics and the
   recorded page-conflict fallback decision.
4. **Version purge — delivered (#910):** `SqlVersionPurgeWorker`'s body is
   real — aborted-writer undo retries plus physical reclamation below the safe
   prune bound; the worker slot was kept in the inventory precisely so this
   landed seam-stable (it did: the activation touched the body and nothing
   else). **Bound decision:** the prune bound is the minimum snapshot floor of
   every open transaction — never a statement-local view, and deliberately
   stricter than the issue's advisory `OldestActive` alone, which can reclaim
   a version a live snapshot with an older minimum still needs (the
   pinned-snapshot test is the proof).

## Non-goals (current cut)

Joins, grouping/aggregation (beyond `COUNT(*)`), subqueries, secondary-index
usage in plans (the B+Tree infrastructure exists — planner adoption is the next
SQL feature), `Serializable` isolation (rejected at begin), and cost-based
optimization. (Row-level MVCC visibility was a non-goal of the first engine cut
and is now delivered — see "The MVCC integration" above.)

## AOT posture

Interpretive evaluation over the AST — no expression compilation, no reflection.
Values are boxed scalars at this layer; span-based row codecs below.
