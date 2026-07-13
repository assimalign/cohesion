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
- **Transactions.** A session's explicit transaction wraps a storage transaction:
  commit is durable via the WAL (no data-page force), rollback restores page
  images in memory. Statements outside a transaction auto-commit. DDL flows to
  the catalog, which self-commits on its own storage (see the catalog DESIGN.md
  for why DDL-in-DML is out of MVP scope).
- **Two file sets per database:** `<name>` (data) and `<name>.catalog` — both via
  the engine's storage strategy, so file-backed and in-memory composition stays
  symmetric.

## Engine-owned background workers (#902)

The engine exposes the five-worker inventory of the root contract's
`IDatabaseEngineWorker` seam — created with the engine (so hosts can claim them
before start) and iterating a lock-free snapshot of every open storage file set
(data + catalog per database, rebuilt when databases open/close; passes tolerate
racing a drop):

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
  skipped and retried next pass.
- **`SqlVersionPurgeWorker` / `SqlIndexMaintenanceWorker`** — documented stubs: the
  engine serializes at page grain (no MVCC version store) and the index layer has no
  compaction yet. They keep the inventory — and any host mapping over it — stable;
  the bodies fill in when those integrations land, without touching the seam.

**Scheduling handoff (single ownership):** `StartAsync` claims and self-schedules
every worker nobody claimed, one dedicated background thread per worker — an
embedded consumer gets identical durability with no host (R10). A host claims
workers before start and drives them on its own execution menu instead. `StopAsync`
quiesces the self-scheduled pumps *before* closing databases, releases the engine's
claims (so a stop-start cycle or a later host can re-claim), and surfaces any worker
fault collected during the run as a `DatabaseException` — a faulted worker never
compromises correctness (grouped commits self-help within the window; checkpoints
simply stop truncating), but the owner must learn the engine ran degraded.

Cadence knobs live here, on `SqlDatabaseEngineOptions`, not on the host — the
engine owns the loop whether or not a host maps it, and hosts read cadence through
`IDatabaseEngineWorker.Interval`.

## The application-builder verb (`AddSqlDatabase`)

The model registers itself on a database application through
`AddSqlDatabase(Action<SqlDatabaseEngineOptions>?)` — an
`extension(IDatabaseApplicationBuilder)` member in `Extensions/`, composing
against the **area root's builder seam only** (this package references no
hosting module; COHRES001 stays intact — the same rule that puts
`AddAuthentication` in `Web.Authentication`, not `Web.Hosting`). The verb
returns the registered `SqlDatabaseEngine` — the Web convention of returning
the feature's own composition object — so a composition root can seed or
provision databases before the application starts. Registration is
dependency-free: the verb news the engine up from options and hands it to
`AddEngine`; no container, no configuration binding.

## Error model

`DatabaseException` (area root) for everything user-facing: plan-time
validation, execution errors, constraint violations (nullability). Parse failures
(`SqlQueryRequest.FromSql`, and therefore the session's text-execute seam) throw
the root's `DatabaseParseException` so callers — the wire-protocol server in
particular — can distinguish fix-the-text errors (`ParseFailure` on the wire)
from execution errors without model knowledge. `SqlCatalogException` (a `DatabaseException`) surfaces
catalog violations unchanged.

## Non-goals (current cut)

Joins, grouping/aggregation (beyond `COUNT(*)`), subqueries, secondary-index
usage in plans (the B+Tree infrastructure exists — planner adoption is the next
SQL feature), row-level MVCC visibility (storage transactions serialize at page
grain; the `Database.Transactions` manager integrates with engine sessions in the
service build-out), and cost-based optimization.

## AOT posture

Interpretive evaluation over the AST — no expression compilation, no reflection.
Values are boxed scalars at this layer; span-based row codecs below.
