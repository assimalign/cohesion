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
  shape). That promise paid out with index adoption: the IR gained exactly one
  node family (`SqlAccessPath` on the SELECT plan — scan | index seek) and the
  executor seam did not move.
- **Access-path selection (rule-based; no cost model — the MVP planner
  contract).** The planner flattens the WHERE clause's top-level `AND`
  conjuncts into per-column sargable predicates — `column op comparand` where
  the comparand is plan-time evaluable (literal/parameter, no column
  references), non-null, and coercible to the column's storage type; `BETWEEN`
  contributes its two bounds — then picks the index with the **longest
  equality prefix** over its leading key columns (ties prefer a usable range
  bound, then uniqueness, then name — deterministic), extended by range bounds
  on the next key column. **Range sargability is a type matrix**: only types
  whose evaluator comparison order provably equals the key codec's byte order
  (integers, decimal, floats, boolean, temporal types) get range seeks;
  strings are equality-only (`Collation.Binary` is code-point order, which
  diverges from ordinal UTF-16 comparison for astral planes — the #854
  lesson), as are Guid/binary/json. Everything else — `OR` at the top level,
  computed columns, column-to-column comparisons, null comparands — falls
  back to the per-object scan. **The full WHERE always remains the residual
  predicate**, re-evaluated on every fetched row, so access-path selection
  can cost performance but never correctness. SELECT only in this cut;
  UPDATE/DELETE target collection still scans (recorded follow-up).
- **Seek execution is snapshot-anchored.** The executor drives the B+Tree
  cursor through the **statement snapshot** (the `IIndex.OpenCursor(snapshot,
  …)` overload — the same snapshot the equivalent scan filters through, which
  is the equivalence anchor under ReadCommitted's per-statement re-capture),
  unpacks each visible entry's packed row location, fetches the row, and
  re-checks the row's stamps against the same snapshot (defense in depth:
  entries mirror row stamps by the maintenance discipline, so a divergence is
  a bug this filter contains rather than surfaces; a dangling entry under an
  invisible stamp is skipped, never fetched wrongly). Prefix ranges ride the
  codec's order preservation: every composite key starting with prefix `P`
  sorts in `[P, successor(P))`; bound inclusivity maps to prefix-successor
  arithmetic on the encoded component. Per-statement observability
  (`SqlStatementMetrics`: access path + records examined) is the behavioral
  proof surface — the planner suite asserts an indexed equality seek examines
  O(matches) records while the equivalent scan examines O(table).
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

## Secondary indexes

`CREATE [UNIQUE] INDEX` / `DROP INDEX` are end-to-end: dialect (the DIALECT.md
matrix), plan nodes, catalog metadata, and B+Tree trees through
`Database.Indexing`'s manager — **on the same database file set** (index pages
ride the data storage's transactional page surface; no new file assets). The
engine is the Indexing child root's first real consumer; the split of duties is
unchanged: the tree is physical, the catalog owns persistence (schema
description + exported registrations), the engine binds them.

- **DDL flow.** CREATE INDEX takes the table's Exclusive lock (DDL-blocking
  build — in-flight writers finish first, Indexing's documented no-online-rebuild
  posture), builds inside one gated **durably committed** bracket (the
  self-committing DDL posture: the catalog record commits independently and must
  never describe a tree a crash could revert), walks **every stored version** and
  inserts entries carrying the version's original writer/deleter stamps — so
  snapshots older than the index read exactly what the row scan shows them —
  then persists metadata + registrations in **one catalog self-commit** (the two
  must never tear: a registration without a description is an unused tree; a
  description without a registration would promise uniqueness no tree enforces).
  Crash windows leave only orphaned tree pages — safe leaks, never a
  half-attached index. DROP INDEX inverts the order (catalog first — the
  authoritative drop — then the in-memory directory) under the same lock; DROP
  TABLE drops its indexes' metadata/registrations atomically with the table
  record and DROP COLUMN on an indexed column is rejected (drop the index
  first — entries key on the column's values).
- **Write-path maintenance mirrors the row-version discipline exactly.** INSERT
  adds entries stamped with the writer's sequence; DELETE stamps entry deleters
  (tombstones — old snapshots keep seeing them); UPDATE tombstones the
  old-location entries and inserts new-location entries, the index image of the
  in-space version chain. Every effect is recorded in the version-store ledger,
  so **logical rollback undoes index stamps through the ledger** (physical erase
  of aborted inserts, deleter-clear of aborted tombstones — the Indexing
  `EraseAsync`/`ClearDeleterAsync` undo surfaces), and the open-time recovery
  scrub purges unproven writers out of every tree in one walk
  (`IIndexManager.PurgeWritersAsync`, driven by the same
  `TransactionRecovery.Analyze` classification that scrubs the record space).
  The ledger route was chosen for live rollback (surgical, O(transaction
  effects)) and the tree walk for open-time scrub (the ledger dies with the
  process) — both end in the same physical operations.
- **The lock-ordering rule** (uniform across INSERT/UPDATE/DELETE so cycles stay
  detectable and rare): phase one acquires the table IntentExclusive lock, then
  row Exclusive locks sorted by packed location, then **unique-index key locks
  sorted by key hash** (`IndexKey.Hash`, the same FNV-1a identity the B+Tree
  locks internally). Inside the apply gate the B+Tree re-acquires the key lock
  as a same-owner re-grant that completes synchronously — **no lock wait can
  ever occur while the gate is held** (a wait there would be invisible to
  deadlock detection). Non-unique indexes take no key locks. Key locks and row
  locks share the `LockResource.Entry` space; a hash/location collision only
  over-locks, and the class ordering keeps acquisition globally consistent.
- **Uniqueness = the B+Tree's latest-state check under the exclusive hashed-key
  lock** (never snapshot visibility — write skew; the recorded #851 lesson).
  Violations surface as the area root's `DatabaseException` at the model
  boundary (`IndexUniqueViolationException` translated — the child-root error
  policy); the statement's bracket has rolled back, the session stays usable.
  Unique keys treat nulls as values (stricter than ANSI; consistent with the
  codec's nulls-first ordering — documented dialect decision).
- **Registrations re-export at persistence points** (root page ids drift on
  splits): index DDL itself, each checkpoint pass, and instance disposal — each
  compares against the stored set first, so an idle checkpoint writes nothing.

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
derivation of the shared server core's guided base
(`Assimalign.Cohesion.Database.Server`'s `DatabaseServer`) fronting exactly one
`SqlDatabaseEngine` (`Create(engine, options)`, options in
`SqlDatabaseServerOptions : DatabaseServerOptions`). Servers are per-model by
design: this type is where SQL-specific wire behavior grows (typed relational
payloads, SQL transaction frames) as the protocol's model-specific surface
lands; today execution rides the model-agnostic text-execute seam on the root's
`IDatabaseSession`, implemented here by `SqlDatabaseSession` with the model's
own parser (`SqlQueryRequest.FromSql`). "Running" lives on the server — the
engine underneath has no lifecycle; the server starts and stops around it.

### The extraction happened here (2026-07-14, the second model server)

The server machinery — accept loop, session state machine and frame pump,
guardrails, two-phase drain — lived **inside this package** from the 2026-07-14
fold (a shared base at n=1 was premature abstraction) until later the same day,
when the second model server (`KeyValueDatabaseServer`) fired the recorded
extraction trigger. The then-proven common core moved to
`Assimalign.Cohesion.Database.Server`; its docs/DESIGN.md carries the
state-machine design record (which moves with the machinery) and the
prediction-vs-evidence table — in short: the predicted core (session table,
guardrails, drain) proved common **and so did the execute pump**, because the
second model rides the root's text-execute seam rather than model-specific
frames, so the extraction scope followed the evidence and this package kept
only the thin derivation.

What remains SQL-specific here: the typed `Engine` property, the
`SqlDatabaseServerOptions` home for future SQL server knobs, the SQL statement
surface behind the text seam, and the model's wire-behavior tests (statements,
parameters, the parse/execution error taxonomy) over a live engine — the
model-agnostic machinery tests moved to the shared core's suite with the
machinery.

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

Joins, grouping/aggregation (beyond `COUNT(*)`), subqueries, `Serializable`
isolation (rejected at begin), cost-based optimization (selection stays
rule-based), index seeks for UPDATE/DELETE target collection, and index-only
result production (a seek always fetches the row). (Row-level MVCC visibility
and secondary indexes — DDL, write-path maintenance, and planner seek
adoption — were non-goals of earlier cuts and are now delivered; see "The MVCC
integration", "Secondary indexes", and the access-path bullets above.)

## AOT posture

Interpretive evaluation over the AST — no expression compilation, no reflection.
Values are boxed scalars at this layer; span-based row codecs below.
