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

## Error model

`DatabaseException` (area root) for everything user-facing: parse failures
(`SqlQueryRequest.FromSql`), plan-time validation, execution errors, constraint
violations (nullability). `SqlCatalogException` (a `DatabaseException`) surfaces
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
