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
- **Scans filter by object id.** Rows encode with the shared tuple codec (#854),
  prefixed by the owning table's object id; all tables of a database share one
  record space and scans decode-and-filter. Per-object page chains are a later
  storage feature; the row format doesn't change for it.
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
- **Transactions (MVCC session binding, §3.8 step 1).** Every statement —
  explicit transaction or auto-commit — runs under an `ITransactionContext`
  from the database's transaction manager, paired one-to-one with a storage
  bracket that **adopts the manager's sequence** (one namespace: the bracket's
  commit record proves the logical transaction at recovery). The per-database
  `SqlTransactionCoordinator` owns the composition (manager + lock manager +
  version store + journal-bound log) and implements `IStorageTransactionSource`,
  the pairing seam. Commit/rollback flow through the *manager*: its journal-bound
  log resolves the paired bracket, so after-images precede the commit record and
  the durability await honors the engine's grouped/synchronous policy; rollback
  restores page images and purges the writer from the version store. Auto-commit
  statements ride a one-statement manager transaction, so visibility semantics
  never fork. Isolation: `Snapshot` (default) fixes the statement snapshot at
  begin, `ReadCommitted` re-captures per statement (the statement scope captures
  `context.Snapshot` exactly once per statement); `Serializable` is **rejected**
  until conflict detection exists (never run weaker than requested). Kernel
  aborts surface wrapped in the root's `DatabaseTransactionAbortedException`.
  Concurrent readers still get **no snapshot filtering** of scans (they can
  observe uncommitted page state) until §3.8 step 2 lands row version stamps.
  On every database open the coordinator runs `TransactionRecovery.Analyze`
  over the recovered journal (the storage strategy defers the open-time
  checkpoint for exactly this) and drives `IVersionStore.PurgeWriterAsync` for
  every unproven sequence; the checkpoint worker checkpoints data storages
  *through the coordinator*, so truncating checkpoint records carry in-flight
  logical sequences. DDL flows to the catalog, which self-commits on its own
  storage (see the catalog DESIGN.md for why DDL-in-DML is out of MVP scope).
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
- **`SqlVersionPurgeWorker` / `SqlIndexMaintenanceWorker`** — documented stubs: the
  engine serializes at page grain (no MVCC version store) and the index layer has no
  compaction yet. They keep the inventory — and any host mapping over it — stable;
  the bodies fill in when those integrations land, without touching the seam.

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

## The per-model server (`SqlDatabaseServer`)

The SQL model ships its own wire-protocol server: `SqlDatabaseServer :
DatabaseServer` (the guided base in `Assimalign.Cohesion.Database.Server` — a
feature-to-feature reference, COHRES-legal), fronting exactly one
`SqlDatabaseEngine` (`Create(engine, options)`). Servers are per-model by
design: this type is where SQL-specific wire behavior grows (typed relational
payloads, SQL transaction frames) as the protocol's model-specific surface
lands; today it adds the typed `Engine` accessor and the composition seam, and
execution rides the base's model-agnostic text-execute path. "Running" lives
here — the engine underneath has no lifecycle; the server starts and stops
around it.

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
- `AddSqlServer(SqlDatabaseEngine, Action<DatabaseServerOptions>)` creates and
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
2. **Row versions + visibility (#908, next):** rows in the shared record space
   grow writer/deleter `TransactionSequence` stamps (the B+Tree leaf-entry
   design is the precedent — tombstone deletes, aborted stamps reverting via
   page images); scans filter through `TransactionSnapshot.IsVisible`, closing
   the dirty-read window.
3. **Row-grain write conflicts (#909):** exclusive hashed-key locks via
   `ILockManager` (the B+Tree uniqueness-lock precedent) replace page conflicts
   as the user-visible surface; deadlock victims surface as
   `DatabaseException`-wrapped aborts at the model boundary.
4. **Version purge (#910):** `SqlVersionPurgeWorker`'s stub body becomes
   `IVersionStore.PurgeWriterAsync` + the `OldestActive` prune bound — the
   worker slot was kept in the inventory precisely so this lands seam-stable.

## Non-goals (current cut)

Joins, grouping/aggregation (beyond `COUNT(*)`), subqueries, secondary-index
usage in plans (the B+Tree infrastructure exists — planner adoption is the next
SQL feature), row-level MVCC visibility of scans (§3.8 step 2 — see "The MVCC
integration" above; the session binding itself is delivered), `Serializable`
isolation (rejected at begin), and cost-based optimization.

## AOT posture

Interpretive evaluation over the AST — no expression compilation, no reflection.
Values are boxed scalars at this layer; span-based row codecs below.
