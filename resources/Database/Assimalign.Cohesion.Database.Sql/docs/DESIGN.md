# Assimalign.Cohesion.Database.Sql — Design

The SQL engine (area architecture: [resources/Database/DESIGN.md](../../../../docs/resources/Database/DESIGN.md)
§3.3): parse (`Sql.Language`) → plan (`SqlPlanner`) → execute (`SqlPlanExecutor`)
against shared storage, with the catalog (`Sql.Catalog`) as schema authority.

## Ordering output values (#1024)

`SqlPlanner.Ordering.cs` binds aliases and standalone numeric ordinals to
projection indexes for stored tables, system relations, joins and groups.
Unqualified aliases take precedence over source columns, including inside
scalar ordering expressions. Qualified references and aggregate operands retain
their source scope. Invalid numeric ordinals fail during binding, even for an
empty input; compound constants such as `1 + 1` remain scalar expressions.

The executor materializes each referenced projection before sorting and retains
source values alongside it for other ordering keys. Grouped queries use the
same binding against completed group outputs. `SqlExpressionEvaluator` resolves
bound AST nodes by identity to these output slots and follows each projection's
source for collation. It does not reconstruct language AST nodes or require
cross-assembly internal access. Sorting is stable, with NULL first ascending and
last descending, followed by DISTINCT and LIMIT/OFFSET. Explicit NULL placement
and derived-table ordering remain capability errors. The exact syntax contract
and execution evidence are in
[DIALECT.md](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md).

## Subquery and insert-source operators (#1021)

`SqlPlanner.Subqueries.cs` binds each uncorrelated child SELECT in its own local
scope and lowers its use sites to typed slots. `SqlSubqueryPlan` owns those child
plans and the enclosing relation plan. `SqlPlanExecutor.Subqueries.cs` executes
each child with the enclosing `SqlStatementContext`, materializes its results,
and supplies typed constants or an IN value list by AST node identity while the
enclosing plan runs. Expression evaluation never opens a query, session,
transaction, or read view. Keeping the expression trees preserves grouping's
expression-ordinal bindings, including subqueries in HAVING and aggregate arguments.

Scalar children require one output column and at most one row; an empty result
is a typed null. IN children require one column and preserve SQL three-valued
membership, including unknown for nonmatches against a null-containing set and
false for membership in an empty set. EXISTS tests row existence. Output types
and string collations cross the materialization boundary. Nested system-view
queries trigger one statement catalog capture, just like outer system views.

`SqlInsertSelectPlan` holds the destination binding and a query source. Its
executor reads the entire source before staging any destination writes, so
self-inserts read the original statement snapshot exactly once. Source width and
declared type compatibility are validated before execution, including empty
sources; value conversions use the literal insert rules. Both insert forms call
the same preparation, default/nullability/CHECK/FK/UNIQUE validation, locking,
index maintenance and transactional apply path.

Correlation is deliberately excluded: qualified outer references receive
`COHDBL001` in language validation, and catalog binding rejects any nested column
that cannot resolve in its local scope. Parsing and planning enforce at most 32
subquery levels. UPDATE/DELETE, VALUES, CHECK/DEFAULT and LIMIT/OFFSET expression
subqueries, quantified comparisons, derived tables, CTEs and lateral joins remain
outside the profile. SELECT continues to require FROM. Subquery resolution uses
AST node identity at evaluation time; it needs no language-assembly friend access
or public AST constructors. See [DIALECT.md](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md#subqueries-and-query-source-inserts-1021)
for the exact supported forms and semantics.

## Compiled-schema provisioning

`ISqlDatabase` implements the root `IDatabaseSchemaProvisioner` seam. Before a
schema is applied, the provisioner requires `EngineModel.Sql`, the same logical
database name, and a shape the shipped SQL DDL surface can represent. It then
reconstructs or reads the last canonical catalog state, uses
`SqlSchemaMigrationPlanner` from `Database.Sql.Schema` for deterministic ordering/destructive gating, and
`SqlMigrationScriptGenerator` for parser-validated engine requests. Supported
steps are table, column, and secondary-index add/drop; alter/rebuild operations
and advanced objects (custom types, functions,
triggers, principals/grants, extensions) fail before execution rather than
recording a false applied hash. Foreign keys and checks now render into provisioning
DDL and persist in the table catalog; unique declarations use unique indexes.

Retained table, column, index and constraint names share the parser's delimited
identifier contract. The renderer preserves bare ordinary names and double-quotes
reserved words or names containing spaces and literal dots. Catalog names contain
the identifier value without its delimiters. Embedded double quotes and null
characters fail before deployment because the current lexer does not support
those identifier forms. The mapper uses the same retained names, so its quoted
DML binds the objects deployed by this renderer without another naming convention.

Each DDL request remains self-committing under the catalog's established rule.
On a later failure, completed reversible steps run their compensating requests
in reverse order and the applied-schema marker remains unchanged. The marker is
written only after all steps succeed, and a repeated apply is a no-op only when
the stored canonical document/hash and live catalog all agree. Full atomicity
for a destructive multi-statement migration is deliberately not claimed: the
current catalog has no transaction spanning DDL statements, and irreversible
data loss cannot be compensated. That kernel seam is recorded as the remaining
gap rather than hidden behind the content hash.

### Object ownership and the schema package

The area root carries only `CompiledSchema` identity and canonical content, the
`IDatabaseSchemaProvisioner` seam, and model-independent ownership contracts.
`Database.Sql.Schema` owns SQL declarations, compiled tables/indexes/constraints,
validation, serialization, and migration plans. The engine references this thin
package; the schema package never references the engine or its storage/transport.

Live sessions create `Adhoc` tables and indexes. The provisioner's private
session carries the compiled schema name as `ProvisioningSchema` in an internal
statement context, so catalog creation persists `Schema` ownership and that name
as `OwningSchema` in the object's first durable record. `SqlCatalogTable.Schema`
remains the separate SQL namespace (for example, `dbo`). Only that internal
session may execute `DROP TABLE`, `ALTER TABLE ADD COLUMN`, `ALTER TABLE DROP
COLUMN`, or `DROP INDEX` against its schema's objects. Normal sessions receive
`DatabaseObjectLockedException` with the object, compiled schema exposed as
`OwningSchema`, and refused operation; row DML remains available. Compensation
uses the same private session and preserves the existing reversible-step policy.

DDL re-reads catalog ownership after acquiring the exclusive object lock. If a
table was dropped and its name reused while the statement waited, the acquired
lock covers the old object id: the statement rejects a schema-owned replacement
and requires a retry for any other replacement before changing metadata or rows.
Index drops likewise refresh their description after the wait. Missing-table
paths fail directly rather than issuing an unlocked catalog mutation by name.

Schema reconciliation compares only objects owned by the applying schema. Ad-hoc
tables and ad-hoc indexes coexist with it and do not create false drift or become
implicit destructive migration targets. A desired name that collides with an
ad-hoc object is rejected before applying any steps; adoption requires a future
explicit policy. Table creation uses the catalog's reserve/build/publish lifecycle,
including its ownership metadata, through the `public static` `SqlCatalog` bridges
rather than any member of `ISqlCatalog`; ordinary `ISqlCatalog.CreateTableAsync`
stays ad-hoc. The catalog persists descriptions;
the engine continues to authorize session operations against schema-owned objects.

## Virtual system relations (C1)

The catalog is the sole data source for six `INFORMATION_SCHEMA` relations and
two Cohesion extensions. Their complete MVP column matrix is in
[DIALECT.md](../../Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md#system-view-matrix-c1).
Every row describes an object in the session's current database. The catalog
name columns contain that database's name; schema qualification continues to
select a SQL namespace, never another database. `TABLES` lists stored tables as
`BASE TABLE`; these virtual relations have no catalog table records to list.

### The relation seam and statement snapshot

`SqlPlanner` recognizes the reserved, schema-qualified view names before
`ResolveTable`. It binds their declared columns into a separate
`SqlSystemViewPlan`, carrying the view identity, projection, predicate, ordering,
distinct/count mode, and limit/offset. `SqlSelectPlan` still means a stored
table, and `SqlCatalogTable` has no virtual/system flag. This keeps physical
access paths and object locks meaningful instead of inventing a dummy object id
or storage location for metadata. No existing public interface changes.

`SqlPlanExecutor.SystemViews.cs` projects catalog descriptions to rows at query
time and applies the same expression evaluator and SELECT semantics as table
queries: column and expression projection, aliases, parameters, `WHERE`,
`ORDER BY`, `DISTINCT`, grouping/aggregates, `LIMIT`, and `OFFSET`. The existing
planner limits on joins and subqueries also apply.
Rows use the ordinary materialized result and wire codecs, so metadata is
available over `SqlDatabaseServer` without a separate protocol operation.

`SqlCatalog.CaptureSnapshot(ISqlCatalog)` returns the catalog-owned
`ISqlCatalogSnapshot` contract, capturing tables and their index descriptions together under the
catalog's existing metadata lock. The capture is read-only, carries no storage
handle, and has no disposal lifetime. A snapshot transaction
retains the capture taken at begin; auto-commit and `ReadCommitted` statements
capture at statement start. All system rows for that statement use that one
capture, including referenced-key resolution. Catalog publication cannot split
a table from its constraints or indexes during a metadata read. Later statements
see completed DDL according to their isolation level, so `DROP TABLE` removes
the table, its columns, constraints, indexes, and ownership rows from fresh
snapshots. There are no metadata rows in the user record space and no persisted
system-view cache to reconcile.

The dependency split keeps virtual relation binding and execution in SQL while
the catalog remains the source of stored object descriptions:

```mermaid
flowchart LR
    Planner["SqlPlanner"] --> ViewPlan["SqlSystemViewPlan"]
    Executor["SqlPlanExecutor.SystemViews"] --> ViewPlan
    Executor --> Snapshot["Sql.Catalog snapshot"]
    Snapshot --> Catalog["Stored table and index descriptions"]
```

### Deliberate extension: index metadata

ISO `INFORMATION_SCHEMA` has no general index relation. Cohesion exposes
`COHESION_SCHEMA.INDEXES`, with one row per ordered index key column. Repeating
the table/index identity on each key row supports ordinary column projection,
filtering, and ordering without parsing a vendor-specific DDL string or encoded
column list. `IS_UNIQUE` and `IS_PRIMARY_KEY` are `YES`/`NO`; the latter
distinguishes the catalog's primary-key enforcement index from other unique
indexes. Physical root pages and index-manager registrations stay internal.
This explicit extension namespace avoids presenting a MySQL `STATISTICS` or
PostgreSQL `pg_indexes` compatibility contract that Cohesion does not implement.

### Deliberate extension: object ownership

`COHESION_SCHEMA.OBJECT_OWNERSHIP` has one row per table or index, identified by
its table identity, `OBJECT_TYPE` (`TABLE` or `INDEX`), and `OBJECT_NAME`.
`OWNER` preserves the catalog values `Adhoc` and `Schema`; `OWNING_SCHEMA` is
the compiled schema's name for code-provisioned objects and null for ad-hoc
objects. It remains distinct from `TABLE_SCHEMA`, the SQL namespace. Index
ownership is reported from the index itself, so an ad-hoc index on a table does
not accidentally inherit that table's provisioning ownership.

These concepts have no ISO counterpart. A separate Cohesion relation keeps
non-standard fields out of `INFORMATION_SCHEMA.TABLES`, preserving the documented
column ordering for tools that map `SELECT *` positionally. The accepted cost
is an additional metadata query when a client needs provisioning ownership.

### Standard vocabulary and the MVP boundary

This is a documented subset of ISO 9075-11's information schema, not a claim to
implement every standard view, column, or SQL domain. Identifiers and descriptive
text use the shared `String` type; standard `yes_or_no` values are strings
`YES`/`NO`, not Boolean values. Cardinal numbers use nonnegative `Int64` values,
with ordinal positions starting at one. These conventions follow the
[information-schema type definitions](https://www.postgresql.org/docs/17/infoschema-datatypes.html)
and [column metadata conventions](https://www.postgresql.org/docs/18/infoschema-columns.html)
documented by PostgreSQL's implementation of the standard.

`COLUMNS.DATA_TYPE` reports the canonical SQL name of the catalog's shared type
identity. The catalog does not preserve the original alias spelling (`INT`
versus `INTEGER`, for example), so this surface cannot reconstruct it. Declared
length, precision, scale, nullability, and default text come from catalog fields
(the default is the stored canonical literal, reported as is);
unknown or inapplicable facts are null, including an unknown character octet
bound. Cohesion types without an ISO spelling, such as `JSONB`, retain their
documented dialect name.

Primary keys and unique constraints project the catalog's enforcing indexes;
foreign keys and explicit checks project persisted constraint descriptions.
For older catalog records with `PrimaryKeyColumns` but no index marked
`IsPrimaryKey`, the constraint views infer the name `PrimaryKey_<table>` from
that existing primary-key declaration, appending `_1`, `_2`, and so on until
the name does not collide case-insensitively with a recorded index or constraint
on that table. This fallback does not invent an index row or index ownership;
separately recorded unique indexes remain `UNIQUE`.
The catalog does not name `NOT NULL` declarations as separate check objects:
their nullability is exposed by `COLUMNS`, and no synthetic check names are
invented. This is a deliberate MVP limitation relative to the standard's
[check-constraint convention](https://www.postgresql.org/docs/18/infoschema-check-constraints.html).
Foreign keys store referenced columns rather than an index identity. The views
resolve those columns to the primary key first (including the legacy fallback),
then to a matching unique index ordered deterministically by name. Foreign keys
report `MATCH_OPTION = 'NONE'`, `UPDATE_RULE = 'RESTRICT'`, and
`DELETE_RULE = 'RESTRICT'` or `'CASCADE'`.
Constraints are enforced immediately and are not deferrable.

Constraint names remain unique within a table, as the catalog already requires,
rather than within the entire SQL schema. Consequently `CHECK_CONSTRAINTS` and
`REFERENTIAL_CONSTRAINTS` can contain ambiguous repeated constraint identities
if callers reuse names on different tables; `TABLE_CONSTRAINTS` and
`KEY_COLUMN_USAGE` carry table identities. This existing catalog limitation is
not changed by introspection; the same portability consequence is described in
[PostgreSQL's information-schema notes](https://www.postgresql.org/docs/18/information-schema.html).
Metadata currently follows the engine's database visibility boundary; per-object
privilege filtering belongs to SQL security (D2).

### Read-only names

`INSERT`, `UPDATE`, `DELETE`, and all supported table/index DDL targeting a
system relation fail with `DatabaseException` and the stable message
`System view '<SCHEMA>.<VIEW>' is read-only.` The view name in the diagnostic is
canonical uppercase, for example `INFORMATION_SCHEMA.TABLES`. The wire server
returns `ExecutionFailure` with that message and keeps the connection usable.
`CREATE TABLE` collisions are refused by the same rule, including
`IF NOT EXISTS`; `DROP TABLE IF EXISTS` and `DROP INDEX IF EXISTS` do not turn
the refusal into a no-op. `CREATE VIEW` and `DROP VIEW` remain outside the
declared dialect and retain their existing unsupported-clause diagnostics.

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
- **Unknown functions fail before binding (#1068).** `SqlPlanner.Plan` walks the
  whole statement, subqueries, DML values and `CHECK` predicates included, and
  rejects a call to any name outside `SqlLanguageProfile.Instance.Functions` with
  `Unknown function '<name>'.` before it binds the catalog or reads a row. The
  evaluator used to discover the name per row, so the same statement failed on a
  populated table and succeeded on an empty one. A declared name that does not
  execute yet (`NULLIF`, `TRIM`, ...) passes this check and still fails during
  evaluation; #1103 rejects those at parse time and gives planner rejections
  structured codes.
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
  (integers, decimal, floats, boolean, temporal types and strings under matching
  byte-expressible collations) get range seeks; Guid/binary/json remain equality-only.
  String predicates with expression/index collation mismatch scan. Everything else — `OR` at the top level,
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
  chains (since record-space format version 3; the current format is 4).**
  Every data record is `[writer u64][deleter u64]` — a fixed 16-byte version-stamp header, the
  B+Tree leaf-entry design adopted for the record space — followed by the
  shared tuple codec payload (#854): the owning table's object id, then one
  self-describing component per column. Why a fixed binary prefix and not
  tuple components (the rejected alternative): (a) stamps in front never
  disturb ADD COLUMN's missing-tail decode, which depends on absent
  components being *trailing*; (b) fixed width makes tombstoning a same-length
  in-place write — a delete can never relocate a record; (c) stamp reads don't
  pay tuple-decode costs on the scan hot path. Since format version 3 the
  tables of a database still share one record *space* but not one page
  stream: rows land on pages tagged with their table's object id (the storage
  layer's per-owner chains), so **a table scan touches only its own table's
  pages** — O(table), not O(database) — and `DROP TABLE` releases the
  table's whole chain back to the allocator (transactionally, inside the
  statement bracket; the record-byte layout is unchanged from version 2, and
  the object-id prefix stays as defense in depth).
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
- **Format rule (data-storage format version, catalog-persisted): exactly one
  format, no upgrade path.** The catalog stores the format version of the whole
  data file set, rows and the index trees that ride it (a kind-4 record for
  versions 1–3, a kind-8 record from version 4). The engine reads and writes
  format 4 only: stamped rows in per-object page chains whose index keys use the
  temporal identity encoding (#1099, below). The earlier versions are history —
  1 = the pre-MVCC unstamped layout, 2 = stamped rows in the shared page stream,
  3 = per-object chains with the `DateTimeKind` and offset inside temporal keys
  (written through 10.0.0-preview.1). `CreateDatabaseAsync` writes the format-4
  marker as soon as the catalog opens, and first checks that the catalog is new
  (no marker, no tables): `ISqlStorageStrategy.CreateStorage` must refuse
  existing storage, and a strategy that reopened it instead would otherwise get
  an older catalog declared current. `OpenDatabaseAsync` refuses a database on
  any other version, older or newer, with a `DatabaseException` (the internal
  `SqlDataStorageFormatException`) that names the database, the version found
  and the version supported. For an older database it says to export the data
  with the engine that wrote it, drop the database and create it again (create
  refuses a name whose storage exists). Version 1 means no marker: every
  released engine stamped one at creation, so an unmarked catalog is a creation
  interrupted before the stamp (or a pre-release database), and the message says
  so. **The gate reads the catalog alone.** `OpenDatabaseAsync` opens the
  catalog file set, loads the catalog and checks the marker before it opens the
  data file set, so a refused open never opens the data files: no crash replay
  into them, no journal or backup file created, no close. A database without a
  catalog storage (an interrupted creation, or one older than the catalog's own
  file set) is refused before any file is opened, never adopted with an empty
  catalog. The catalog file set itself gets only what opening any storage does.
  Cleanly closed, it is left byte-identical, because a storage closed with
  nothing written through it writes nothing (Storage DESIGN.md). Crashed, it
  gets the storage layer's physical redo/undo, which is format-agnostic and
  idempotent, and keeps its journal. That matters because the old engine must
  still be able to open the database to export its data, including running its
  own transaction recovery over journals the refused open did not touch.
  `SqlDataStorageFormatTests` pins both cases: clean images stay byte-identical,
  and a crashed format-3 image keeps its data files and catalog journal and
  still recovers (its uncommitted writer scrubbed) once its own engine opens it.
  **Decision (owner, 2026-10-01): no upgrade path while the line is
  pre-release.** #1099's first implementation rebuilt temporal indexes on open
  and carried UNIQUE duplicates the new identity exposed; it was withdrawn
  together with the earlier in-place stages (1 → 2 stamping, 2 → 3 chain
  relocation), and upgrades are designed fresh in #1152 (in-place or offline,
  duplicate handling, crash safety, progress). **Downgrade fence:** engines
  before format 4 never compared the marker against a newer version, so the
  format-4 marker is a record kind their catalogs refuse to load; they fail the
  open instead of writing format-3 keys into a format-4 database.
- **Schema evolution (#1023):** `ADD COLUMN` validates the literal default and
  current rows under the exclusive object lock before publishing the complete
  replacement definition in one catalog transaction. Backfill is resolved at
  read time from persisted column metadata; no existing row bytes, locations or
  MVCC stamps are changed. `SqlRowCodec` reports how many fields were physically
  present, and the executor resolves only the missing tail, on both scans and
  index seeks. Explicit NULLs remain NULL. Default conversion is shared with
  omitted-column INSERT values and rejects overflow, nonfinite floats, string
  truncation, and decimal rounding or precision loss. Validation scans are
  O(table); there is no physical backfill or cross-storage row/catalog commit.
  A crash before catalog publication leaves the old definition; after publication
  the same persisted default resolves old rows on reopen. Constrained additions
  still build enforcing indexes durably before publication; a crash can leave
  unpublished index pages, never a partly backfilled table.
  Bound plans retain their immutable definition; later statements bind the full
  new definition even with an older row snapshot. NOT NULL without a default
  fails if any current row exists; historical deleted versions can still read
  NULL under an older snapshot after an addition to a currently empty table.
  `DROP COLUMN` rewrites positional records, materializing surviving defaults
  while preserving version stamps. DDL is self-committing and refused in explicit
  transactions. Schema-owned tables retain their existing live-session DDL guard.
  The persisted default is the canonical SQL text of its literal (`'it''s'`, `5`,
  `TRUE`), and the value every read and INSERT coerces comes from the table
  version's bound form, parsed once (see [Persisted definitions](#persisted-definitions-canonical-text-parsed-once)).
- **Expression evaluation** is interpretive with SQL null propagation (nulls
  reject predicates, comparisons with null are null, `AND`/`OR` are three-valued
  and skip the right operand once `FALSE AND` or `TRUE OR` decides the result),
  overflow-checked BIGINT arithmetic for exact integers and promotion to decimal
  otherwise, ordinal string comparison, hand-rolled `LIKE`
  (`%`/`_`), `CASE`, `BETWEEN`, `IN` (lists), `IS NULL`, parameters (`@name`
  bound by bare name), and a small builtin set (`COALESCE`, `UPPER`, `LOWER`,
  `LENGTH`, `ABS`). Compiled expression plans are a later optimization.
- **Arithmetic faults are coded statement failures (#1069).** Division or
  modulo by zero raises `SqlEvaluationException` with `COHSQLE001`; a result,
  operand, literal, aggregate or CASE/COALESCE value outside its numeric type
  raises it with `COHSQLE002` (the dialect's arithmetic fault contract). Each
  operator, negation and `ABS` codes its own fault at the source. The evaluator's
  entry point, `Evaluate`, then converts any remaining `ArithmeticException`
  from the expression tree (an oversized literal, for example), so no raw runtime
  fault leaves evaluation. Recursion runs through `EvaluateCore`, below that
  boundary, so `CAST` still reports an operand it cannot represent as a
  conversion failure. Aggregate accumulation, the result-type normalization of
  projected values, and store assignment into an integer or `DECIMAL` column
  (`CoerceForColumn`) code their own overflow the same way. A nonzero REAL or
  DOUBLE divisor that converts to Decimal zero is out of range, not a division
  by zero, because zero is judged on the operand as supplied. `SortRows` unwraps
  the `InvalidOperationException` the runtime sort puts around a throwing
  comparer, so incomparable `ORDER BY` keys fail the statement with the
  comparer's own `DatabaseException` instead of ending a wire session. The
  exception is an internal `DatabaseException` whose message leads with the code, the convention
  Graph's `COHDBG` codes use, until the area root grows a structured diagnostics
  carrier. Evaluation runs in a write statement's first phase, before any
  physical bracket opens, so a fault writes nothing. The session's ordinary
  failure path then applies: auto-commit rolls back, and an explicit transaction
  stays active.
- **Every recursive walk checks the stack (#1151).** The parser bounds a statement
  at 128 levels of expression nesting, but the engine also accepts typed requests,
  whose trees the language package's internal constructors could build to any
  depth. So every walker that recurses over an expression tree calls
  `RuntimeHelpers.EnsureSufficientExecutionStack()` before it descends: the
  session's system-relation scan (`UsesSystemView`), the planner's validators and
  binders (`RejectUnknownFunctions`, `ValidateExpression`, `StaticOperandType`,
  `ContainsAggregate`, `ContainsCast`, `ReferencesAnyColumn`, `RejectColumnReferences`,
  `ContainsStar`, the sargable and join
  equality collectors, ordering alias binding, grouping binding, `SameGroupExpression`,
  `GroupExpressionType`, the subquery source walk and `PlanSubqueries`), the
  evaluator (`EvaluateCore`, `FindCollation`), CHECK validation
  (`ValidateCheckSyntax`), and persisted-definition binding and comparison
  (`SqlPersistedExpression.Bind`, `AreEquivalent`, `SqlBoundTableCache`'s column
  collector). `SqlPlanner.Children` documents the rule for the next walker.
  `LIKE` matching recurses once per `%` it backtracks through, so its depth follows
  the values, and both matchers check too. The check costs a stack-pointer
  comparison per node; a parsed tree trips it only on a thread created with a small
  stack. `SqlDatabaseSession.ExecuteAsync`
  turns the resulting `InsufficientExecutionStackException` into
  `SqlEvaluationException` with `COHSQLE004` (ISO SQLSTATE 54001, statement too
  complex), so it takes the ordinary failure path above instead of ending the
  process; over the wire it is an `ExecutionFailure` and the session stays ready.
  The tests prove the checks without a tree deeper than the limit, which only those
  internal constructors could build and which this engine's tests do not reach:
  `SqlExpressionDepthExecutionTests` runs a walk of a 128-level tree with a few KB of
  stack left before the check would fail, measured on that thread with
  `RuntimeHelpers.TryEnsureSufficientExecutionStack`. A walker that checks as it
  descends throws within its first levels; one that did not check would carry on
  into the runtime's reserve, which a 128-level walk fits inside, and complete, so a
  missing check fails the test rather than the test process.
- **Signs require numbers; unary plus is the identity (#1068 follow-up).** The
  evaluator returns a unary-plus operand unchanged (value and CLR type; NULL
  propagates), while negation still widens exact integers to BIGINT, and
  `GroupExpressionType` reports the matching result types. A sign whose operand is
  not a number raises `SqlEvaluationException` with `COHSQLE003`. When the operand's
  type is fixed by the plan — a string or Boolean literal, a column, a predicate,
  `||`, `UPPER`/`LOWER`, a CAST, a bound scalar subquery — `SqlPlanner.ValidateExpression`
  raises it before execution (`StaticOperandType`), so the result does not depend on
  whether the table has rows; it and `COHSQLE005` (below) are the planner's own
  coded rejections, and count evaluation and stack checks can also raise
  `COHSQLE001`/`002`/`004` while planning. A parameter, CASE or other operand only
  its value types fails when evaluated, with the same code and message.
- **VALUES rows and LIMIT/OFFSET counts have no column scope (#1165).** Both are
  evaluated once, against an empty row: a VALUES row before the INSERT writes
  anything, a count while planning. ISO SQL forbids a column reference in an
  INSERT's table value constructor. The planner used to bind no VALUES expression,
  and the executor evaluated each one with the target table's columns in scope, so
  `INSERT INTO u (id, a) VALUES (id, 1)` resolved `id` to ordinal 0 and indexed the
  empty row; the `IndexOutOfRangeException` was not a `DatabaseException`, so over the
  wire it reached the `Internal` catch-all and closed the session. Now
  `SqlPlanner.ValidateScopelessExpression` checks every expression of every VALUES row,
  and each count, before anything executes. It rejects a column reference anywhere
  (under a sign, CAST, CASE, aggregate or function call, qualified or not) with
  `SqlEvaluationException` `COHSQLE005`, which names the reference and the clause
  (ISO SQLSTATE class 42, syntax error or access rule violation). It then rejects an
  aggregate and `*` with uncoded messages naming the clause, and runs
  `ValidateExpression` against an empty column scope, which raises `COHSQLE003` for a
  sign over a non-numeric literal or parameter and `COHDBL001` for a subquery. The
  column check runs first, so `-a` and `SUM(a)` report the column, not the
  operand type or the aggregate. Every row is checked before planning returns, so a
  fault in a later row leaves the earlier rows unwritten. As defense in depth,
  `ExecuteInsertAsync` evaluates VALUES with an empty column scope too, so its
  evaluator can never resolve an ordinal the empty row does not have.
  Parameters and expressions over literals and parameters are unchanged. A count
  with a column reference already failed while planning, as `Unknown column`, or,
  inside a subquery, as an error result: `PlanSubqueries` turned the child's
  `Unknown column` into `SqlUnsupportedQueryException`, which `SqlQueryExecutor`
  returns as a `QueryResultStatus.Error` result carrying `COHDBL001`'s
  correlated-subquery diagnosis. It now throws `COHSQLE005` at every nesting level,
  for a reference to the subquery's own columns or the outer query's: ISO SQL's
  fetch-first count is a simple value specification, which no column of any scope
  reaches. (An explicit outer qualifier is still the parser's `COHDBL001`.) A `*` in
  a count now reports `'*' is not allowed in LIMIT.` (or `OFFSET`) instead of the
  evaluator's `Expression 'SqlStarExpression' is not supported by the executor yet.`
  The unqualified
  `DEFAULT` keyword in VALUES is not in the dialect and parses as a column reference
  named `DEFAULT`, so it reports `COHSQLE005` too. The niladic datetime functions
  `CURRENT_DATE`, `CURRENT_TIME` and `CURRENT_TIMESTAMP` also parse as names, because
  they take no parentheses; `RejectColumnReferences` reports an unqualified name
  spelled as one of them as the unsupported function (`Function '<name>' is not
  supported by the executor yet.`, the message `NOW()` gets at evaluation), not as
  a column, until the parser gives them a function node.
- **SELECT materializes.** Sorting and `DISTINCT` need the full result anyway at
  this stage; `SqlMaterializedResultSet` carries typed columns and evaluated
  rows. Streaming operators arrive with the planner build-out.
- **Transactions (MVCC session binding, §3.8).** Every statement — explicit
  transaction or auto-commit — runs under an `ITransactionContext` from the
  database's transaction manager, whose sequences come from the storage's own
  counter (one namespace). The shared `Database.Transactions.TransactionCoordinator` owns
  the composition (manager + lock manager + record-space version store +
  journal-bound log). The instance's thin `IStorageTransactionSource` adapter
  resolves a context's *current statement bracket* and retains the engine's
  `DatabaseException` for a missing pairing. Commit flows
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
  sequences. DDL in auto-commit mode flows to the catalog, which self-commits on its own storage
  (see the catalog DESIGN.md for why DDL-in-DML is out of MVP scope), and
  interlocks with row writers through table-grain intent locks (below).
- **Shared record-space composition (#918).** `SqlTransactionRecordSpace`
  supplies row reads, transactional updates/deletes, and the existing packed
  location codec to `RecordSpaceVersionStore` in `Database.Transactions`.
  `SqlRowCodec` retains SQL tuple encoding, while
  its stamp operations delegate to the shared `RecordVersionStamp` contract
  ([layout](../../Assimalign.Cohesion.Database.Transactions/docs/DESIGN.md#record-stamp-prefix-the-16-byte-contract)).
  `RecordVersionIndex` in Indexing binds each live secondary index to the
  shared undo ledger. Recovery ordering is unchanged: check the data-storage
  format, re-attach indexes, analyze and scrub records, scrub indexes with the
  same classification, then complete the deferred checkpoint.
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
  detectable and rare): phase one acquires the adjacent tables' IntentShared
  locks in object-id order, then the table's IntentExclusive lock, then row
  Exclusive locks sorted by packed location, then **parent-row Shared locks
  sorted by (object, entry)** for the outgoing foreign keys the statement takes
  on and for the ones it releases, then
  **unique-index key locks sorted by key hash** (`IndexKey.Hash`, the same FNV-1a
  identity the B+Tree locks internally). Object-grain locks are intent-only
  across classes one and two, and intent modes are mutually compatible, so only
  the entry-grain classes can conflict. Inside the apply gate the B+Tree
  re-acquires the key lock as a same-owner re-grant that completes synchronously
  — **no lock wait can ever occur while the gate is held** (a wait there would be
  invisible to deadlock detection). Non-unique indexes take no key locks. Key
  locks and row locks share the `LockResource.Entry` space; a hash/location
  collision only over-locks, and the class ordering keeps acquisition globally
  consistent. **The one deliberate exception is the cascade walk**, which locks
  rows in discovery order because the closure it is discovering is what
  determines the lock set; see "Referential enforcement locks parent rows" under
  constraints for why that trade was taken and what it costs.
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
implementation of the area root's `IDatabaseServer` contract fronting exactly
one `SqlDatabaseEngine` (`Create(engine, options)`, options in
`SqlDatabaseServerOptions`). Servers are per-model by design: this type is
where SQL-specific wire behavior grows (typed relational payloads, SQL
transaction frames) as the protocol's model-specific surface lands; today
execution rides the model-agnostic text-execute seam on the root's
`IDatabaseSession`. "Running" lives here — the engine underneath has no
lifecycle; the server starts and stops around it.

### Why the machinery is Sql-internal — per-model duplication (2026-07-14, owner decision; the settled placement)

The server machinery — accept loop, session state machine and frame pump,
guardrails, two-phase drain — lives **inside this package** (`Server/` for the
public surface, `Internal/` for the pump and context), not in a shared library.
This is the fifth and final placement, and the history is deliberate evidence
discipline (each move recorded in the area DESIGN decision log): folded into
`Database.Hosting` (2026-07-12) → resurrected as a shared `Database.Server`
base (2026-07-13) → folded in here as premature abstraction from n=1
(2026-07-14), with an extraction trigger recorded for the second model server →
**the trigger fired the same day**: building `KeyValueDatabaseServer` supplied
the evidence, the evidence exceeded the prediction (even the execute pump
proved common, because model #2 rides the root's text-execute seam), and the
proven core was extracted back into `Database.Server` → **the owner reviewed
the evidence and chose per-model duplication anyway** (2026-07-14): the shared
library was removed, and each model package carries its **own full copy** of
the machinery. The trade-off was stated and accepted — model independence
(each model server free to evolve its pump, framing, and guardrails without
coordinating a shared base) outweighs the duplication/drift cost; **wire-behavior
parity is maintained by the protocol contract and per-model E2E suites, not by
shared code**. The copies are allowed to be textually near-identical today —
divergence over time is sanctioned, and no linked-source or shared-internals
mechanism may be used to fake the independence. The prediction-vs-evidence
table from the extraction is preserved in the area `DESIGN.md` §3.10. The root
contracts (`IDatabaseServer`/`IDatabaseServerContext`/`IDatabaseServerSession`)
remain the **only area-wide requirement** — every model implements them its own
way against `Connections` and the `Database.Protocol` child root (via the
root's rollup).

### Composition seam

`SqlDatabaseServer.Create(engine, options)` — or the `AddSqlServer(engine,
configure)` builder verb — composes a server. The options carry one configured
`IConnectionListener` instance, not a listener factory. `StartAsync` explicitly
awaits `BindAsync` before it starts the accept loop or returns; a bind failure is
propagated only after the listener is terminally disposed, so a partially
acquired endpoint cannot remain live. `StopAsync` cancels the pending accept,
drains sessions within the configured budget, then terminally disposes the
listener. Listener ownership therefore transfers to the server when start is
attempted. Stop is terminal: restart symmetry is a fresh server with a fresh
listener, never reuse of the disposed pair.
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

Implementation decisions (carried over from the machinery's prior homes — this
record moves with the machinery):

- **Version negotiation:** an unknown *major* in `Startup` earns
  `UnsupportedVersion` and a close; the server then speaks
  `ProtocolVersion.Current` (minors are additive by the protocol's contract, so
  no per-minor branching yet).
- **Database binding** resolves on the server's one engine: already-open
  databases first (`TryGetDatabase`), then an open attempt; an exact
  `DatabaseNotFoundException` → wire `DatabaseNotFound` and close. A database
  the format gate refuses (`SqlDataStorageFormatException`, #1099) → wire
  `Unavailable` carrying the engine's refusal message and close, so a remote
  client learns the format found, the format supported and the remedy; the
  message is engine-authored and names only the database the client asked for.
  Other open failures propagate to the handshake's internal-error path. (The pre-per-model
  server probed a *list* of engines in registration order; one engine per server
  removed that ambiguity.)
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
  violation). Evaluation faults (division by zero, numeric overflow) are
  `DatabaseException`s carrying a `COHSQLE` code, so they take this path and
  the session stays ready (#1069; before that fix a raw `DivideByZeroException`
  reached the `Internal` catch-all and closed the session, and so did the
  `InvalidOperationException` the runtime sort wraps around an `ORDER BY` key
  comparison that fails). A column reference in an `INSERT ... VALUES` row is a
  planning error coded `COHSQLE005` and takes the same path (#1165; before that fix
  the `IndexOutOfRangeException` it raised during evaluation closed the session as
  `Internal`). A statement nested deeper than the dialect's 128-level
  expression limit is a `ParseFailure` (`SQL0006`); before #1151 a 200,000-term
  expression overflowed the stack and ended the server process, every session with
  it. Framing/order violations (`ProtocolException`, malformed parameter
  components) → `ProtocolViolation` **and close**; anything unexpected →
  `Internal` and close. A child-root exception that escapes raw (for example a
  `StorageException` the engine failed to wrap) reaches the wire as `Internal`
  and closes the session — the engine's model boundary is where wrapping into
  `DatabaseException` belongs.
- **`ResultComplete` carries the result set's real `AffectedCount`** — the
  evidence-driven adjustment from the (since-reversed) shared-core extraction,
  kept in both models' copies: SQL's materialized query sets report `-1`, so
  SQL wire behavior is unchanged, while outcome-shaped sets elsewhere carry
  real counts.
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
replication endpoints; no special transaction frames (SQL transaction commands
use the existing `Execute` payload); no
TLS/transport policy — transport configuration stays in `libraries/Connections`
drivers, while the server owns bind-through-release lifecycle.

## SQL transaction control and the B7 seam

`BEGIN [TRANSACTION]`, `COMMIT [TRANSACTION]`, and `ROLLBACK [TRANSACTION]`
are ordinary SQL requests over the existing wire `Execute` message. The session
dispatches their parsed AST before creating an auto-commit transaction. `BEGIN`
opens a coordinator context; subsequent queries and DML share that context and
its visibility snapshot. `COMMIT` awaits the coordinator's durable commit;
`ROLLBACK` undoes row and index versions and releases locks. Closing the session,
including EOF, explicit wire termination, or server shutdown, rolls back an open
transaction before releasing the session. The C# transaction API and SQL control
commands operate on the same scope.

The session owns `Stack<SqlTransactionScope>`, with zero entries outside a
transaction and exactly one root entry in B2. Each scope carries its transaction
and the existing `Database.Transactions.IsolationLevel` value. The session's
default isolation value is `Snapshot`; begin and auto-commit pass that value to
the coordinator instead of embedding a level at each call site. B7 anticipates
`SAVEPOINT name`, `ROLLBACK TO [SAVEPOINT] name`, `RELEASE [SAVEPOINT] name`, and
`SET TRANSACTION ISOLATION LEVEL ...`: named scopes and undo markers can extend
the stack, while isolation syntax sets the carried value. B2 implements none of
that syntax or savepoint undo machinery. `Serializable` remains rejected by the
existing C# seam until the coordinator supports serialization detection.

The session lifecycle below applies equally to SQL requests and C# transactions.

```mermaid
stateDiagram-v2
    [*] --> Idle
    Idle --> Open: BEGIN
    Open --> Open: query or DML
    Open --> Idle: COMMIT or ROLLBACK
    Open --> Closed: disconnect rolls back
    Idle --> Closed: disconnect
```

State misuse returns `QueryResultStatus.Error` with an error diagnostic, without
throwing or changing the active transaction. `COHSQLT001` means nested `BEGIN`;
`COHSQLT002` means `COMMIT` or `ROLLBACK` without an active scope. The wire maps
these to `ExecutionFailure` and includes the stable diagnostic code in its
message. The connection remains usable.

DDL still commits on the catalog's separate storage and cannot enlist in a user
transaction through the frozen catalog contract. `COHSQLT003` therefore refuses
`CREATE`, `ALTER`, and `DROP` while a session transaction is open, before catalog
or data mutation. Auto-commit DDL retains its established durability contract.
This limitation and the unavailable interface seam are recorded in
`_out/phase4-b2-ESCALATIONS.md`.

## Integrity constraints

`UNIQUE` is a unique index, consistently across SQL syntax, catalog metadata and
`CompiledSchemaIndex(IsUnique: true)`. A separate compiled constraint kind would
duplicate both persistence and enforcement and permit the two paths to diverge.
Named `UNIQUE` declarations keep their name as the index name. Foreign keys and
checks are table constraint records; their names, ordered columns, reference
target, delete action and canonical check expression text are persisted in the
versioned table codec and recovered with the rest of the catalog (see
[Persisted definitions](#persisted-definitions-canonical-text-parsed-once)).

Table creation reserves an unpublished object identity, durably builds its
unique and primary-key backing trees, then publishes the table, constraints,
index metadata and registrations in one catalog commit. A crash before publish
can leave orphan tree pages, but cannot expose an unenforced declaration.
Adding column constraints or table constraints uses the same atomic publication
path after validating existing rows. Primary-key backing indexes carry an
explicit catalog marker; schema reconciliation keeps them separate from the
schema's declared secondary indexes.

Primary keys use the same physical uniqueness enforcement, with a persisted
`IsPrimaryKey` marker on their backing indexes. Schema reconciliation excludes
only those marked indexes from the declared secondary-index list; an explicitly
declared unique index over the same columns remains a separate schema object.
Compiled provisioning creates all tables and indexes before adding references,
so child-first names and cyclic reference graphs provision deterministically.

`SqlPlanExecutor` enforces checks and outgoing references on inserted or updated
rows, and incoming references on parent deletes or key changes. Checks reject a
false result; SQL unknown/null passes. Foreign keys with null components are
not checked (MATCH SIMPLE). A non-null child key needs a parent visible through
the statement snapshot. Delete defaults to `RESTRICT`; `CASCADE` recursively
collects child deletions into the same statement apply bracket. Parent key
updates are restricted while referenced. `ON UPDATE` is unsupported and returns
`COHDBL001`.

Checks require Boolean predicates made from supported deterministic row
expressions. Parameters, subqueries, aggregates, unsupported functions and casts
are rejected when the DDL declares the check (`SqlPlanExecutor.ValidateCheck`); the
load path binds a stored predicate with `BindPersistedCheck`, which does not apply
the declaration rules again (see
[Persisted definitions](#persisted-definitions-canonical-text-parsed-once)). The
CHECK shape walk visits each node once, so its cost is linear in the predicate.
Multiple unnamed constraints receive distinct generated names. Cascades are collected before restriction checks; a child
already in the complete statement deletion set does not prevent that deletion,
so physical row order and declaration order do not change the result.

Enforcement uses the existing lock manager and coordinator. Unique keys acquire
transaction-duration key locks before entering the apply gate, then the B+Tree
checks current entry stamps, including winners committed after the caller's
snapshot. Two transactions cannot both publish the same unique key. Constraint
failures roll back the physical statement bracket, preserving earlier successful
statements in an explicit transaction. The exception is
`SqlConstraintViolationException`; callers can inspect the constraint name,
table name and safe offending value. Sensitive/raw binary values are omitted
where they cannot safely be exposed.

Uniqueness follows the existing index convention: null components participate
in the key, including its duplicate check. A single numeric or Boolean offending
value is carried when available; strings, binary values and composite keys are
omitted, including the encoded index exception that could disclose them.

**Referential enforcement locks parent rows, not reference graphs.** Every
constraint check needs one guarantee from the lock manager: *before a statement
reads a table's latest state for some key, every other writer of that key is
already decided* — committed, or rolled back with its undo complete. Two
conflicting locks on the **parent row** deliver it:

- A child writer (INSERT, or an UPDATE that sets a foreign key) takes `Shared`
  on the parent row version it matched, then re-checks that version's stamps
  under the lock. Holding it is what stops a concurrent parent delete from
  admitting an orphan; the stamp check is what rejects a parent the writer's own
  snapshot still sees but a committed transaction has already removed.
- A child writer that **releases** a reference — deleting the row (including as
  a cascade target), or changing its key away — takes the same `Shared` lock on
  the parent row it is giving up. This half is not symmetry for its own sake: a
  latest-state read treats an *undecided* tombstone as absence, so without it a
  parent delete concludes the row is unreferenced, commits, and is contradicted
  the moment the child's transaction rolls back and restores the reference.
  `ParentDelete_ConcurrentUncommittedChildDelete_ShouldNotStrandTheRestoredChild`
  is the regression guard. The cascade walk skips the edge it arrived by, whose
  parent row the statement already holds exclusively — that is also what keeps
  the constraint-lookup access-path metrics unchanged.
- A parent writer takes `Exclusive` on every row it deletes or re-keys **before**
  reading child tables for incoming references. `Shared` and `Exclusive` are
  incompatible, so by the time that read happens every child writer that acquired
  *or released* a reference to the row has been decided.

Table-grain locks stay intent-only: `IntentShared` on the adjacent tables a
statement reads for constraint purposes, which is compatible with other writers'
`IntentExclusive` and blocks only table-grain DDL, keeping parent definitions
stable for the life of the statement. A cascade walks the closure locking as it
descends — a row is exclusively locked before its children are read — because a
transitive closure cannot be pre-sorted.

**Why not component-wide exclusion (the rejected original).** The first cut took
`Exclusive` on every table in the transitive closure of the reference graph, in
object-id order, before row/key locks and the apply gate. It was correct and
could not deadlock, but in a normalized schema that closure is usually the whole
database, so a single foreign key serialized nearly every writer — the cost was
the feature, not an edge case. Narrowing to parent-row granularity buys back
that concurrency (`ForeignKeyComponent_ConcurrentWritesToDifferentTables_ShouldNotSerialize`
is the proof: writers of different tables in one component now overlap, and two
children may hold shared locks on the same parent row at once) and pays for it
in one place — **referential waits can now form wait-for cycles**, where the
closure's object-id ordering made them impossible. They surface as the lock
manager's requester-closes-cycle abort, the retryable
`DatabaseTransactionDeadlockException` the row-write path already produces, so
the failure mode is one callers must already handle. The MVCC alternative —
validating references optimistically and rechecking at commit — was not taken:
the coordinator has no commit-time validation hook, adding one would be a new
concurrency mechanism in `Database.Transactions`, and lock-based enforcement
reuses the machinery that already arbitrates every other wait in the engine.

DDL constraint backfills are the exception that stays table-grain: `ADD
CONSTRAINT` / `ADD COLUMN` validate *every* existing row against latest state,
so they hold the `Exclusive` table lock on their own table plus the `Exclusive`
locks on every referenced parent — the same guarantee at table grain, and DDL is
rare enough that its cost is not the engine's concurrency story.

Reference lookups select the longest usable leading-column index prefix and
reorder equality values to match its column order. Partial prefixes retain a
residual comparison for remaining columns; they scan the table only when no
suitable index exists. A declared index
whose physical tree is missing fails closed.

## Persisted definitions: canonical text, parsed once

**The rule (owner decision, 2026-10-01: production-ready regardless of the
pre-release).** Every SQL expression the catalog persists is stored as canonical
SQL rendered from its parsed tree, never as the user's text: CHECK predicates and
literal column DEFAULTs today. Expression DEFAULTs (#1121) and view queries
(#1124) must take the same path when they land; a new persisted expression may
not store source text, parse it on a per-row or per-statement path, or grow a
second renderer or parser entry. The motivation is #1068: tightening the parser
made text that an older, more lenient parser had accepted unparseable, and because
the engine stored CHECK as written and re-parsed it on every validated write, such
a constraint would have failed every write to its table, with "drop the constraint
and add it again" as the only remedy.

- **One helper, `SqlPersistedExpression`.** `Canonicalize` renders the canonical
  text with `SqlExpressionRenderer` (Sql.Language), re-parses it, and requires the
  result to be structurally equal to the declared tree (`AreEquivalent`) before
  anything is stored; a mismatch fails the DDL as an engine defect instead of
  writing a definition that would not reload. `Load` parses persisted text as the
  predicate of a carrier query and requires it to be exactly one expression — a
  trailing clause or a second statement is damage, not a definition.
  `LoadDefaultValue` additionally requires one non-NULL literal. `Bind` resolves a
  loaded expression against the row shape it is evaluated over (below).
  BindConstraints canonicalizes CHECK; the planner canonicalizes literal DEFAULTs,
  so `DEFAULT 'it''s'` stores `'it''s'` and `DEFAULT +5` stores `5`. The DEFAULT's
  runtime meaning is unchanged: the literal's value text is coerced to the column
  type exactly as before, and CREATE TABLE now proves that coercion succeeds for
  every column before it reserves the table, as ADD COLUMN already did, so a
  DEFAULT the column cannot store fails its DDL rather than every later INSERT
  that omits the column.
- **Parse once per table version (`SqlBoundTableCache`).** The database owns one
  cache keyed by the `SqlCatalogTable` instance in a `ConditionalWeakTable`. A
  catalog table description is immutable and every change publishes a new
  instance, so the key *is* the table's schema version: ADD/DROP CONSTRAINT,
  ADD/DROP COLUMN and DROP + CREATE produce new keys, and a replaced version's entry
  is collected with it — invalidation is structural, with no hook to forget. A
  `SqlBoundTable` holds the bound CHECK predicates (with the column ordinals a
  violation reports) and each column's DEFAULT value. `SqlPlanExecutor.ValidateRows`
  evaluates the cached predicates, `DecodeRow` and the INSERT path resolve defaults
  from the cached values, and `EnsureCanDropColumn` re-binds the cached predicates
  against the remaining columns. No write, read or DML statement parses catalog
  text; `BindCount` lets tests prove it.
- **Binding is not re-validation.** DDL accepts a CHECK through
  `SqlPlanExecutor.ValidateCheck`: binding, plus the declaration rules (no casts,
  no sign over an operand the plan types as non-numeric). Loading binds it through
  `BindPersistedCheck`: columns and collations resolve (`SqlPersistedExpression.Bind`),
  the predicate is a row expression (no parameter, subquery, `*` or aggregate) and a
  Boolean predicate over the functions the evaluator implements — what evaluating
  it needs, and nothing more. The engine that stored a predicate accepted it, so a
  later release that tightens a declaration rule must not turn the predicate into
  an open failure, as #1068's parser tightening would have done to stored text.
  `CHECK (-label IS NULL)` on a TEXT column, which DDL now rejects with
  `COHSQLE003`, still opens and is enforced as stored; a row it cannot evaluate
  fails its own statement with the evaluator's coded error. The rule for future
  changes: **a rule that narrows what DDL accepts goes in `ValidateCheck` only; a
  change that removes the engine's ability to evaluate a construct a stored
  definition may hold (an evaluator function, an operator) is a catalog-format
  change** and needs a format version and a migration, never a silent open failure.
  `EnsureCanDropColumn` binds the same way, so a tightened rule cannot block an
  unrelated DROP COLUMN.
- **Linear validation.** `ValidateCheckSyntax` visits each node of a predicate once,
  passing each child the Boolean requirement its position imposes (AND/OR/NOT
  operands, and COALESCE arguments and CASE results when the call or CASE must be
  Boolean). It used to walk a Boolean operand once as a plain child and again as a
  Boolean one, which doubled the work per AND/OR level: once binding moved to open,
  a 24-term AND took 22 s to open, and a 40-term one did not finish its DDL in 100 s.
  `Check_LongConjunction_ShouldDeclareOpenAndEnforceInLinearTime` declares, reopens
  and enforces a 127-term AND, the longest flat conjunction the 128-level
  expression nesting limit (#1151) accepts.
- **The nesting limit cannot strand a definition (#1151).** The DDL parses with the
  dialect's 128-level expression limit, and `Canonicalize` re-parses the canonical
  text with the same parser before storing it. The canonical text has the declared
  tree, and `SqlExpressionRenderer` adds at most one pair of parentheses around a
  node, so its parentheses nest no deeper than that tree, which the DDL already
  held to 128 levels; a definition the DDL accepted therefore always loads at open,
  even where the renderer adds parentheses the declaration did not have. A CHECK of
  126 signs over a column, which the renderer stores with 125 nested parentheses,
  declares, reopens and is enforced (`Check_AtLimit_ShouldPersistReopenAndEnforce`).
  Format 4 is unreleased, so no stored definition predates the limit. Reading the
  deepest definition back needs a few hundred KB of stack (the parser's cost is in
  the Sql.Language design); on a thread with less, the parser reports `SQL0007`
  rather than `SQL0006`. `Load` and the bind then raise an
  `InsufficientExecutionStackException` that names the definition and says the
  catalog is not damaged, never the restore-from-backup hint, and the caller decides
  what it means: `BindCatalog` fails the open, adding that the database should be
  opened on a thread with a larger stack, while inside a statement (a DDL
  re-parsing canonical text, or a table version bound on first use) the session
  fails the statement with `COHSQLE004` like any other walk out of stack.
- **When binding happens.** `SqlDatabaseInstance` binds every table right after
  the catalog opens and the format checks pass, before recovery or the index
  manager touch the data file set. Each DDL binds the version it publishes before
  the statement returns — CREATE TABLE before publishing it, ADD CONSTRAINT and
  constrained ADD COLUMN through the backfill's validation of the replacement it
  then publishes, and DROP COLUMN on the instance the catalog returns. DROP
  CONSTRAINT and unconstrained ADD COLUMN publish the catalog's own copy built from
  the same column and constraint instances, which adopts the bindings of the
  version it came from (`Adopt`) without parsing again. A version that reaches a
  statement unbound (a table a test created through the catalog directly) binds on
  first use.
- **Fail fast at open.** Canonical text always reloads, so a definition that does
  not parse, is more than one expression, is not a literal (DEFAULT), or no longer
  binds to its table means the catalog is damaged or came from an incompatible
  engine build. The open fails with `Database '<name>' cannot be opened.`, naming
  the table and the constraint or column and telling the operator to restore from
  a backup; nothing is half-opened, because binding precedes every other
  component. Canonical storage is part of data-storage format 4, which is
  unreleased, so there is no further version bump and earlier text is not
  migrated.
- **Earlier formats are refused before binding.** A format 1–3 catalog holds
  definitions as written — a DEFAULT as the literal's bare value — which binding
  would misread: `true` reloads as the Boolean literal `TRUE` (silently changing a
  TEXT default, even for old rows that lack the field), `+5` as `5`, and `abc` as
  a column reference reported as catalog damage. The format gate
  (`ThrowIfFormatIsNotCurrent`, see "Format rule") refuses every database not on
  format 4 before `BindCatalog` runs, so no pre-canonical definition is ever bound.
  `BindCatalog` runs after that gate on open and after the format marker is written
  on create; keep that order.
- **Compiled schemas.** A compiled CHECK keeps its author's spelling in the
  schema document and hash. `SqlSchemaProvisioner` compares it with the catalog
  by canonical form (`CanonicalCheck`), so reapplying an unchanged schema stays a
  no-op, and reconstructing the live schema reuses the desired spelling for a
  canonical-equal predicate so reconciliation plans no change for it.
- **`INFORMATION_SCHEMA`.** `CHECK_CONSTRAINTS.CHECK_CLAUSE` and
  `COLUMNS.COLUMN_DEFAULT` report the canonical text.

## Application composition (Phase 29)

`AddSql(Action<IDatabaseApplicationContext, ISqlDatabaseEngineBuilder>)` is an
`extension(IDatabaseApplicationBuilder)` member in this model package. It captures
one factory and returns the application builder. Application Build invokes the
callback with the build-time root context and a model builder; no model registration
uses DI, configuration binding, Hosting, or a container. The model builder exposes
all SQL options, including `FileSystemPath? RootPath`, and permanently freezes them
when its one Build attempt begins. Direct `SqlDatabaseEngine.Create(options)` stays
supported for standalone use.

Workers and servers are nested deferred factories on `IDatabaseEngineBuilder`.
Build creates the operational engine first, executes worker factories against it,
then server factories. Workers are pumped on engine-owned threads through
`IDatabaseEngineWorker.Run`; this genuinely model-agnostic registration/execution
is why the base builder interface earns its place. No strongly typed factory
overloads are added: a model server factory can cast its supplied engine once;
duplicate delegate overloads would introduce lambda ambiguity without adding a
construction capability. The former `AddSqlDatabase` and sibling application
`AddSqlServer` verbs are replaced by `AddSql` with nested `AddServer`.

`IDatabaseEngine.Servers` exposes the resulting read-only server collection.
Application Build snapshots it for start/stop only. The engine owns these factory
products and disposes servers in reverse order before quiescing workers and closing
databases. Application-owned factory engines transfer ownership at successful Build;
instance-registered engines remain caller-owned. Failed factory construction cleans
up accepted products, rejected products, and the engine; independent cleanup failures
are aggregated. Async cleanup reached through synchronous Build/Dispose runs without
the caller's synchronization context. Database name operations now accept
`DatabaseName`, including SQL's collation-specific creation overload.
No production hosting code currently consumes `IDatabaseEngineBuilder` generically.
Its shared contract is retained for model-independent `AddWorker` composition;
model-specific options stay on each derived builder interface.

`SqlDatabaseEngine.CreateBuilder()` returns `ISqlDatabaseEngineBuilder`.
This interface-first entry enables standalone nested composition and lets the concrete
hosting-aware engine factory configure the same builder from its final configuration
and services. The model still sees no DI or configuration contract.

## Error model

`DatabaseException` (area root) for everything user-facing: plan-time
validation and execution errors. Named integrity violations use
`SqlConstraintViolationException`, a `DatabaseException` carrying the constraint,
table and safe offending value. Parse failures
(`SqlQueryRequest.FromSql`, and therefore the session's text-execute seam) throw
the root's `DatabaseParseException` so callers — the wire-protocol server in
particular — can distinguish fix-the-text errors (`ParseFailure` on the wire)
from execution errors without model knowledge. Opening a database absent from the
storage strategy throws the root's `DatabaseNotFoundException`; opening one the
data-storage format gate refuses throws the internal `SqlDataStorageFormatException`
(a `DatabaseException`, format rule above); other open failures
retain their own error type. `SqlCatalogException` (a `DatabaseException`) surfaces
catalog violations unchanged. Arithmetic faults throw the internal
`SqlEvaluationException` (a `DatabaseException`) whose message leads with
`COHSQLE001` (division by zero) or `COHSQLE002` (numeric value out of range);
a sign over a non-numeric operand throws it with `COHSQLE003`, from planning when
the operand's type is known there or the operand is a parameter whose bound value
is not a number, and a statement whose walk runs out of stack throws it with
`COHSQLE004` (#1151). The codes are published in the dialect's
diagnostics table. A persisted CHECK or DEFAULT that does not load fails the open
with a `DatabaseException` naming the database, table and constraint or column. No runtime
`ArithmeticException` escapes expression evaluation.

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
   See "Row format" and "Format rule" under the execution model.
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

Outer/multi-table joins beyond the supported two-table INNER JOIN, correlated
and derived-table subqueries, explicit NULL placement, `Serializable`
isolation (rejected at begin), cost-based optimization (selection stays
rule-based), index seeks for UPDATE/DELETE target collection, and index-only
result production (a seek always fetches the row). (Row-level MVCC visibility
and secondary indexes — DDL, write-path maintenance, and planner seek
adoption — were non-goals of earlier cuts and are now delivered; see "The MVCC
integration", "Secondary indexes", and the access-path bullets above.)

## Database scope conformance (A5)

Each session captures one database instance and its catalog/executor. Table
qualification selects a schema inside that catalog; it cannot select another
database or a server object. Database creation, enumeration, and deletion belong
to the host-owned `IDatabaseEngine`, not to SQL session execution.

`SqlDatabaseScopeTests` guards this boundary using two databases with the same
table name and different values. Text and typed requests cannot read or mutate
the other database, and attempted `USE`, database DDL, database enumeration, and
server shutdown leave the binding and engine inventory unchanged. These are
behavioral tests; they do not use reflection or widen the session contract.

## AOT posture

Interpretive evaluation over the AST — no expression compilation, no reflection.
Values are boxed scalars at this layer; span-based row codecs below.

## Model-owned wire family (#1015)

This package owns the Sql request and tabular result codecs; the shared protocol
contains only mechanism. [Wire format](WIRE-PROTOCOL.md) specifies every message and
scalar component for independent clients. The server binds SqlProtocol.Family
once on accept, retains wire version 1.0 and the existing bytes, and negotiates
incompatible majors before authentication. Result materialization belongs to
Database.Sql.Client. The TCP Listen(Uri) extension lives in the optional Database.Sql.Tcp composition package; this engine references only generic Connections.

Payload offsets are zero-based and exclude the shared five-byte frame header. `Execute` (5)
starts with a signed 32-bit big-endian statement byte length `S` at bytes 0–3, followed by
`S` UTF-8 statement bytes at byte 4 and a nonnegative signed 32-bit big-endian parameter
count at bytes `4 + S`–`7 + S`. Each parameter beginning at byte `Q` has a nonnegative
signed 32-bit big-endian name length `N` at bytes `Q`–`Q + 3`, `N` UTF-8 name bytes at
byte `Q + 4`, a nonnegative signed 32-bit big-endian encoded-value length `V` at bytes
`Q + 4 + N`–`Q + 7 + N`, and `V` self-describing scalar-component bytes at byte
`Q + 8 + N`. `ResultHeader` (6) starts with a nonnegative signed 32-bit big-endian column
count at bytes 0–3; each repeated column has the same four-byte name length and UTF-8 name,
followed immediately by one unsigned `DatabaseType` byte. `ResultRow` (7) concatenates one
self-describing scalar component per result field from byte 0 through the payload end, with
no count prefix. `Transaction` (9) is reserved and has no implemented payload; SQL
transaction commands travel as statement text in `Execute`.

`ResultComplete` (8) is the implemented family's one fixed-width payload. Its encoder emits
exactly eight bytes: bytes 0–7 (bits 0–63) are the signed 64-bit affected count in big-endian
order. SQL uses `-1` when the returned row stream is the count and otherwise reports the
statement's affected count. The packet view below shows that complete fixed-width payload.

```mermaid
packet-beta
0-63: "Affected count (i64, big-endian)"
```


## Collation (Phase 17, #1025)

The [approved collation design](../../../../docs/programs/COLLATION_DESIGN.md)
now executes across comparisons/LIKE, sorting, grouping and DISTINCT, and all unique
index writes/locks/seeks. Binary is the database default. The concrete engine
creation overload accepts a different default, which is persisted independently
from nullable column overrides. Column and expression `COLLATE` use pinned Unicode
17.0 byte transforms; original row spelling is preserved. Innermost explicit
expression overrides take precedence over column overrides and database defaults.

B+Tree keys, seek bounds, unique-key locks and backfill duplicate detection share
the same encoding. Expression overrides that differ from the indexed column scan.
Legacy Invariant is scan-only; new indexes and indexed constraints reject it clearly.
Grouping and DISTINCT use the effective collation for both equality and hashing.
Foreign-key string columns require equal effective collations so forward and reverse
checks agree. Default changes after table creation reject until index rebuild support
exists. Other model defaults are unaffected. See the design for the legacy
CompareInfo compatibility escalation and #1026 linguistic-collation boundary.

## Temporal key identity (#1099)

`TIMESTAMP` keys are wall-clock ticks without the `DateTimeKind`; `TIMESTAMPTZ`
keys are instants without the offset — the identity `SqlValueComparer` compares
by (explicit `DateTime.Ticks`/`DateTimeOffset.UtcTicks` branches, with matching
hashes for grouping and DISTINCT). Like collation, the rule is applied once, in
`SqlRowCodec.AppendKeyValue`, which every key path shares: B+Tree maintenance,
seek prefixes and range bounds, unique-key locks, and build/backfill duplicate
detection. The key is the Types identity form (`SpecifyKind(Unspecified)`,
`ToUniversalTime()`), so the component layout and decoding are unchanged; rows
keep the round-trip value encoding. Because key equality now equals evaluator
equality for both types, they are range-sargable and join-seekable. The rule
changed the on-disk key format to data-storage format 4, and a format-3
database is refused at open rather than rebuilt (format rule above; upgrades
are #1152); the dialect contract is DIALECT.md "Temporal value identity".
