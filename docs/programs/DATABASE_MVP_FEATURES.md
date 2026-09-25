# Database MVP — Feature List

**Status:** signed off 2026-09-17 · **Owner:** Chase Crawford · **Branch:** `feature/L03.02-mvp-engines`
**Companion to:** `docs/programs/DATABASE_PROGRAM_PLAN.md` (the technical sequencing index)

> **Why this file exists.** The program plan is written for implementation sessions — it speaks in
> issue numbers, lanes, and dependency gates. This file is the *review* layer: what each piece of
> work actually gives somebody using the database, expressed so it can be tracked and signed off
> without opening 60 GitHub issues. Every feature here maps to real work items; nothing is invented.
>
> When the MVP lands, fold the durable parts into `resources/Database/DESIGN.md` and delete this.

---

## 1. Where things actually stand

Measured from source, not from the plan. Line counts are production code (`src/`), excluding tests.

| Engine | Production code | Tests | Verdict |
|---|---|---|---|
| **SQL** | ~14,300 lines at B1 baseline; B2 extends engine, language, catalog and schema | ~6,900 at B1 baseline; B2 and later phases add acceptance coverage | **Working within a measured subset: 33 of 49 declared clauses (Phase 22).** ORDER BY projection aliases (including nested expressions) and select-list ordinals execute over the wire, with alias-first collisions and precise invalid-ordinal errors (#1024). ALTER TABLE literal defaults now backfill populated-table reads and omitted inserts through the wire; unsupported expressions and invalid defaults reject atomically (#1023). Phase 18 adds uncorrelated `IN`/`NOT IN`, `EXISTS`/`NOT EXISTS`, scalar subqueries and transactional `INSERT ... SELECT`, including server/client execution (#1021). Phase 17 added executable column/expression `COLLATE`, persisted database defaults, and collation-consistent seeks, uniqueness, grouping and hashing (#1025). GROUP BY/HAVING, COUNT/SUM/AVG/MIN/MAX (#1020), SQL transactions, referential integrity, and the exact scalar CAST subset (#1022) execute. B2 and the dialect matrix state partial-support boundaries and the 16 excluded clauses. |
| **Key-Value** | ~6,500 lines across engine, client, catalog, storage | ~2,700 | **Working.** Storage, commands, server, client all landed. |
| **Documents** | engine, OQL parser, planner, chunked storage, catalog | 276 at engine baseline; Phase 13 adds execution conformance | **Working within a measured OQL subset: 8 of 12 declared clauses** *(engine landed `6085bad3`, `a4770b3f`)*. Phase 13 measures every advertised clause through a live engine, including grouping/aggregates/HAVING and `CREATE INDEX` / `DROP INDEX`, and fails CI for an unmapped profile addition. Nested documents, arrays, mixed-shape collections, collection ownership. |
| **Graph** | engine, GQL parser, traversal planner, adjacency storage, catalog, server and typed client | 189 at engine baseline; Phase 13 adds execution conformance; Phase 31 adds client/server acceptance | **Working within a measured GQL subset: 8 of 27 declared clauses** *(engine landed `1092d6b2`)*. Phase 31 (#1013) serves scalar `MATCH`, `CREATE`, `DELETE` / `DETACH DELETE`, and unchanged catalog `SHOW` through `GraphDatabaseServer` and `Graph.Client`. The engine now produces real paths for a single bound node, relationship, or named MATCH path projection; `ExecutePaths` preserves identities, labels, types, properties and traversal order. Ownership, cycle, database-scoping and statement-failure reuse rules remain enforced. Wire transaction control is deliberately absent; explicit transactions remain an in-process session API. This is not complete ISO/IEC 39075 support. |
| **Blob** | engine, chunked storage, catalog, wire server and streaming client | 39 at engine baseline; the wire fixture adds client/server streaming coverage | **Working** *(engine landed `b97a9976`; wire server and streaming client `f9666be8`)*. Chunked persistence, atomic publication, streaming reads/writes proven at 128 MiB under a 64 MiB heap, crash-durable, container ownership enforced. The client streams uploads and downloads through the shared streaming exchange (#214); the wire fixture moves 256 MiB through the real client/server under a 64 MiB managed heap. |
| **Cache** | 6 lines | 6 | Out of MVP scope by prior decision. |

**Phase 30 — SQL comparison (#1029):** predicates, ordering, DISTINCT, grouping and extrema
share one comparator. Binary values order by unsigned byte sequence; floating comparisons
retain adjacent values and the full finite range. Exact mixed numeric ordering and explicit
NaN, signed-zero and infinity rules are recorded in the SQL dialect. Regression coverage drives
the same data through filtering and ordering/deduplication, with binary and large-double
predicates also exercised through the SQL server and client. The mapper builder restrictions
and `COHMAP003` review remain deferred with mapper work (#1007/#1008).

**Shared kernel — landed and in use by all five engines:** durable page store with CRC and crash
recovery, write-ahead journal, MVCC with snapshot isolation and deadlock detection, the shared
per-database MVCC composition (extracted in `31047f3a` before three engines could each grow their
own copy), B+Tree secondary indexes, the shared type system with order-preserving encodings, the
execution pipeline, and the wire protocol with a pooled client.

**The honest summary: all five engines work.** Each composes the kernel rather than
re-implementing paging, journaling, or locking, and each is crash-durable, ownership-enforcing, and
scoped to a single database. Blob forced the kernel to grow large-object support it had been
missing; nothing after it needed a kernel change.

**The wire-format decision is implemented** (#1015): each endpoint binds one immutable model
family before startup, preserving the shared envelope and handshake. SQL, Key-Value, Blob, and
Graph now have production servers and clients. Graph's Phase 31 transport dispatches its existing
scalar and path message families to the real engine; it does not reconstruct paths from scalar
rows. Documents still needs its production query server/client. Graph wire transaction control
remains a deliberate limit: neither transaction statement text nor reserved byte 9 is supported.
See §3.4 of `docs/resources/Database/DESIGN.md`.

### What is open in GitHub

| Area | Open items | Note |
|---|---|---|
| Documents | #190–#192 + epics | #181–#189 close on merge (`6085bad3`). Client, security, replication remain |
| Graph | #201–#204 + epics | #193–#200 close on merge (`1092d6b2`); **#193 answered: ISO/IEC 39075 GQL**. Phase 31 (#1013) delivers the server/client graph transport. Security, replication and wire transaction control remain |
| Blob | #212, #215, #216 + epics | Engine and streaming client landed (#211, #213, #214 closed); security, replication and large-object interoperability tests remain |
| Key-Value | #206, #919 (+ #208–#210 Cache, post-MVP) | Security and TTL only |
| SQL | #176, #177 (+ 5 epics) | Security; migrations deferred |
| Kernel / cross-cutting | #161, #861, #862, #918 | Backup/restore, encryption, embedded, MVCC extraction |
| Hosting / tooling | #167, #168, #857–#859, #973 | **Deferred by your rule 4 and rule 7** |

---

## 2. The feature list

Each feature states the user-visible outcome first, then the work behind it. **Status** is one of
`DONE`, `PARTIAL`, `NEW` (not yet filed as a work item), or `OPEN` (filed, unstarted).

### Theme A — Foundation corrections

Structural fixes that must land before engine work, because every engine inherits them.

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **A1** ✅ | **Every database object knows who created it** | A table created by your C# schema code is marked as code-owned. A table created by running `CREATE TABLE` is marked as SQL-owned. The engine records the difference and persists it in the catalog. | `DONE` | #1000 |
| **A2** ✅ | **Code-owned objects cannot be altered or dropped by ad-hoc statements** | If your application provisions a `Customers` table from C#, then `DROP TABLE Customers` over a SQL connection is refused with a clear error naming the owning schema. Only a schema deployment can change it. Objects created by plain SQL stay fully mutable by plain SQL. | `DONE` | #1000 |
| **A3** ✅ | **Schema provisioning moves out of the shared root into the SQL model** | Today the area root carries `IDatabaseSchemaTable`, `IDatabaseSchemaColumn`, triggers, functions, grants and a migration planner that switches on engine model. Relational vocabulary in a model-agnostic root is the violation you flagged. It moves to the SQL model; each other model gets its own provisioning shape, or none. | `DONE` | #1001 |
| **A4** ✅ | **The multi-root structure is written down** | Documentation stating that `Assimalign.Cohesion.Database` is the area root, that `*.Sql.*`, `*.Blob.*`, `*.Documents.*`, `*.Graph.*`, `*.KeyValuePair.*` are model families inheriting it, and what may live in which. Prevents the next session repeating A3. | `DONE` | #1001 |
| **A5** ✅ | **No server-scoped query language, anywhere** *(guards landed for all five engines: SQL, Key-Value, Blob in `b97a9976`; Documents in `6085bad3`; Graph in `1092d6b2`)* | A connection binds to exactly one database and cannot address another. Already true at the wire protocol — the startup handshake takes a database name. This feature makes it a *guarded* property: a conformance test per model proving no statement or command can reach across databases or reach the server. | `DONE` | #1003 |

> **A5 note.** Creating and dropping databases stays on `IDatabaseEngine` in C#. That is host-side
> composition — the code that owns the engine process — not a client-facing API, and no client or
> language surface exposes it. If you want that removed too, say so; it is a larger change.

### Theme B — Query language platform

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **B1** ✅ | **Each model opts into the clauses it supports** | The shared language package today hands every model the same lexer and a flat keyword list. B1 adds a capability profile: a model declares which clauses it accepts, and anything outside the profile produces a precise "not supported by this model" diagnostic instead of a generic parse failure. | `DONE` | #1002 |
| **B2** ✅ | **A published, complete SQL surface** | Phase 4 adds wire-accessible `BEGIN` / `COMMIT` / `ROLLBACK`, durable foreign keys with delete cascade/restrict, row checks and unique indexes with concurrent-write enforcement. Phase 22 measures **33/49 clauses** through a live engine, with profile-driven execution conformance that fails for a clause without a passing case. Two stored tables execute `INNER JOIN ... ON` through the server/client, with one MVCC snapshot and secondary-index probes where equality is safe (#1019). Grouping is a distinct plan above the filtered table or join input; COUNT/SUM/AVG/MIN/MAX and HAVING execute with null semantics and wire coverage (#1020). Distinct subquery and insert-select plans execute uncorrelated `IN`/`NOT IN`, `EXISTS`/`NOT EXISTS`, scalar projection/predicate subqueries and transactional `INSERT ... SELECT` over the wire under that same snapshot, with literal-insert constraint enforcement (#1021). CAST performs exact conversions with target metadata and wire coverage (#1022). COLLATE executes over the wire: binary, case-insensitive and case/accent-insensitive byte transforms agree across predicates, ordering, grouping/DISTINCT hashes and UNIQUE indexes; database defaults and column overrides survive restart (#1025). ALTER TABLE ADD COLUMN resolves literal backfills from durable metadata without rewriting old MVCC versions, applies defaults to omitted inserts, preserves explicit NULL rules and column collation, and rejects expressions or invalid values before mutation (#1023). ORDER BY projection aliases (bare and nested) and one-based select-list ordinals execute across stored tables, system relations, joins, grouping, DISTINCT and pagination through the wire; unqualified aliases win collisions, and invalid numeric ordinals reject precisely (#1024). The remaining language groups and partial-support boundaries below keep the broader feature partial. DDL remains self-committing and is refused inside explicit transactions. | `PARTIAL` (measured Phase 22 execution subset) | #172, #173, #174; catalog constraint portions of #175 / #177; #1019, #1020, #1021, #1022, #1023, #1024, #1025 |

> **What the SQL profile executes (measured 2026-09-18, Phase 22).**
>
> **Measured and advertised (33 of 49):** `SELECT` `INSERT` `UPDATE` `DELETE` `CREATE TABLE` `CREATE INDEX`
> `ALTER TABLE` `DROP TABLE` `DROP INDEX` `FROM` `WHERE` `GROUP BY` `HAVING` `ORDER BY` `JOIN`
> `LIMIT` `OFFSET` `VALUES` `CASE` `CAST` `BEGIN` `COMMIT` `ROLLBACK`
> `TRANSACTION` `FOREIGN KEY` `REFERENCES` `CHECK` `UNIQUE` constraint `CONSTRAINT`
> `CASCADE` `RESTRICT` `COLLATE` `SUBQUERY`. Each clause has a passing execution case, with exact value,
> state, or intended semantic-error assertions. A conformance test enumerates the
> profile and fails if a clause has no case. OQL and GQL have the same guard.
> The earlier figure of 32 counted `JOIN`, `GROUP BY`, `HAVING`, and subqueries,
> which parsed but did not execute. Phase 13 removed the no-op CAST; Phase 14 restored it after real conversion and wire verification (#1022), measuring 28/48. Phase 15 restores `JOIN` for its executable two-table inner form (#1019). Phase 16 restores `GROUP BY` and `HAVING` after aggregate execution and wire verification (#1020). Phase 17 adds `COLLATE` to both the declared set and live execution profile, measuring 32/49. Phase 18 restores `SUBQUERY` for the executable uncorrelated forms below, measuring 33/49 (#1021). Phase 21 expands the existing ALTER TABLE execution case to populated-table literal backfill and atomic default rejection (#1023). Phase 22 expands ORDER BY to projection aliases, including nested scalar references, and select-list ordinals, with complete server/client result verification (#1024); the named-clause count remains 33/49 because these are extensions of advertised clauses.
>
> **COLLATE boundaries:** `binary`, `case_insensitive`, and `case_accent_insensitive` use pinned Unicode 17.0 transformations. Database default < column override < innermost expression override applies to WHERE/LIKE, ORDER BY, GROUP BY, DISTINCT, and UNIQUE/index keys. Expression/index mismatch scans. Legacy `invariant` remains linguistic and scan-only; new indexes and indexed constraints under it reject clearly. The persisted default is set when the database is created and is fixed thereafter; changing an existing indexed database default requires a future rebuild mechanism. No culture-aware/custom/session/full-text collation surface; full linguistic indexing is #1026.
>
> **CAST boundaries:** signed Int8/Int16/Int32/Int64 and Decimal convert to each other or String; String converts to those numerics, Boolean, or String; Boolean converts to Boolean or String. All other pairs error. Null propagates with target metadata. Invalid text, overflow, fractional-to-integer loss, excess decimal precision/scale, and string overflow error. Decimal uses System.Decimal; p=1..28 and s=0..p. String length counts UTF-16 units; CHAR does not pad. No rounding or truncation. Unknown types are rejected during parsing. DEFAULT/CHECK casts remain rejected; full aliases, text grammar, and wire metadata boundaries are in the dialect matrix.
>
> **JOIN boundaries:** exactly two stored tables support `INNER JOIN ... ON` or the equivalent `JOIN ... ON`, including self joins with distinct aliases. Both inputs and `ON` use the same statement MVCC snapshot. Qualified names and aliases bind across both inputs; ambiguous unqualified columns are errors. Joins compose with `WHERE`, `ORDER BY`, `LIMIT`, and `OFFSET`; row order without `ORDER BY` is unspecified. The planner can probe a secondary index on either input using mandatory equality predicates on a leading key prefix; the full `ON` remains a residual predicate. Types whose encoded key equality differs from SQL equality use a buffered nested loop with O(left rows × right rows) comparisons. `LEFT`, `RIGHT`, and `FULL OUTER`, `CROSS`, `NATURAL`, `USING`, missing `ON`, more than two inputs, and joins involving virtual system relations report `COHDBL001`.
>
> **Grouping and aggregate boundaries:** `WHERE` filters input rows before grouping; `HAVING` filters completed groups before `ORDER BY`, `OFFSET`, and `LIMIT`. One or several scalar expressions may form a key, including over the supported inner join. Every source column outside an aggregate must be grouped; invalid projections, HAVING, and ORDER BY expressions fail before reading rows. NULL keys form one group. `COUNT(*)` counts rows; `COUNT(expr)` skips NULL and returns Int64. `SUM`, `AVG`, `MIN`, and `MAX` skip NULL and return NULL for all-null input. An empty ungrouped aggregate has one result row (zero counts, other aggregates NULL); empty grouped input has no rows. SUM/AVG accept numeric values and accumulate/return System.Decimal (`DatabaseType.Decimal`); integer AVG retains fractions. AVG uses decimal division, rounded to the nearest representable decimal with midpoint-to-even (up to 28 fractional digits). Numeric overflow errors; MIN/MAX preserve the input expression's type. Aggregate DISTINCT/ALL, aggregate-local ORDER BY/FILTER, ordered-set aggregates, GROUPING SETS/ROLLUP/CUBE, and windows report `COHDBL001`. Phase 17 implements the COLLATION_DESIGN.md foundation: group equality and hashing share the effective column/expression/database collation. The original binary grouping cases remain, with case-insensitive and accent-insensitive counterparts (#1025).
>
> **Subquery and INSERT SELECT boundaries:** uncorrelated single-column `IN`/`NOT IN`, `EXISTS`/`NOT EXISTS`, and single-column scalar subqueries execute in SELECT expressions, including projections, WHERE, JOIN ON, GROUP BY/HAVING and ORDER BY. Both outer and inner queries retain their supported ORDER BY/LIMIT/OFFSET behavior and share the outer statement's MVCC snapshot. A matching IN value wins even if another result is NULL; otherwise a NULL candidate makes IN/NOT IN unknown and WHERE rejects the row. Empty IN is false and empty NOT IN is true, including a NULL left operand. Empty EXISTS is false and NOT EXISTS is true. A scalar result is NULL for no rows and errors for more than one row. Nesting supports at most 32 subquery levels; deeper input reports `COHDBL001` before recursive execution. Correlation is excluded: references outside the inner FROM/JOIN scope receive a precise `COHDBL001` diagnostic. Quantified ANY/ALL/SOME comparisons, derived tables, CTEs, lateral joins, subqueries in UPDATE/DELETE or INSERT VALUES, and subqueries used as LIMIT/OFFSET expressions also report `COHDBL001`. CHECK continues to reject every subquery.
>
> `INSERT ... SELECT` validates source/target column counts and declared type compatibility, including an empty source; each actual value passes the same conversion and nullability checks as a literal insert. Omitted columns receive the same defaults. CHECK, UNIQUE and foreign keys use the literal-insert enforcement and transactional statement bracket; a failed statement leaves no partial insert. Source rows are fully materialized under the statement snapshot before destination writes, so self-inserts read the original source once. SELECT sources compose with the supported joins, grouping, subqueries, ordering and pagination; explicit transaction commit/rollback applies normally.
>
> **Partial support is explicit:** SELECT requires one table or system relation, or the two-table inner join described above;
> grouped and ungrouped `COUNT`, `SUM`, `AVG`, `MIN`, and `MAX` execute (#1020); supported clause counts
> do not imply support for every function in the lexical vocabulary. INSERT accepts
> `VALUES` or the SELECT sources described above (#1021). CASE supports simple/searched row expressions.
> Referential actions are `ON DELETE CASCADE`/`RESTRICT` only; `ON UPDATE` is rejected.
> ALTER TABLE supports ADD/DROP COLUMN and ADD/DROP CONSTRAINT. Added-column literal
> defaults backfill populated-table reads from persisted catalog metadata and apply
> to subsequent omitted-column inserts; old row bytes and MVCC stamps are preserved.
> Explicit NULL follows nullability. A populated-table NOT NULL addition without a
> non-null default fails, as do defaults that cannot convert or fit the declared type.
> Nonliteral defaults receive CREATE TABLE's precise rejection before mutation.
> String defaults retain their original text and use the added column's COLLATE.
> The complete definition publishes atomically and survives reopen. Bound plans
> retain their old shape; later statements use the new definition even over older
> row snapshots. Ordinary table schemas are not transaction-pinned. A NOT NULL
> addition without a default is allowed when no current rows exist; historical
> versions visible only to an older snapshot then retain NULL in the missing field.
> Schema-owned tables remain protected and DDL in explicit transactions remains
> refused (#1023). ORDER BY accepts source expressions, output aliases (bare or nested), and select-list
> ordinals across supported stored/system/join/grouped sources, including DISTINCT and LIMIT/OFFSET (#1024).
> Unqualified aliases take precedence over same-named source columns; qualified names select the source.
> Referencing a duplicated output alias is ambiguous. Aggregate arguments retain their source scope.
> Standalone positive integers identify output columns; zero, negatives, out-of-range values and non-integer
> numeric literals error. `ORDER BY 1 + 1` is a constant expression, not ordinal 2. NULL sorts first in ASC
> and last in DESC. Explicit NULLS FIRST/LAST, GROUP BY ordinals, and derived-table output ordering report
> `COHDBL001`; supported scalar subqueries remain executable. Phase 13's alias/ordinal gap is closed by
> execution cases whose complete results differ from insertion and scan order.
> UNIQUE treats NULL as an equal index key: a second NULL is a violation.
> These failing partial forms are not counted as execution evidence. Detailed boundaries
> appear in the [SQL dialect matrix](../../resources/Database/Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md)
> and the Phase 13 audit notes (`_out/phase13-NOTES.md`).
>
> **Not advertised (16):**
>
> | Group | Missing |
> |---|---|
> | **Set operations** | `UNION` · `INTERSECT` · `EXCEPT` |
> | **CTEs** | `WITH` · `RECURSIVE` |
> | **Window functions** | `OVER` · `PARTITION BY` · `WINDOW` |
> | **Views** | `CREATE VIEW` · `DROP VIEW` |
> | **Joins** | `NATURAL` · `USING`; outer and cross forms are excluded from the supported `JOIN` subset |
> | **Other** | `TOP` · `ALL` · `FETCH` · `RETURNING` |
>
> **The two engine gaps closed by Phase 4:**
>
> 1. **Transaction control in the language.** A client connected through `SqlDatabaseServer`
>    can begin a transaction, issue multiple statements, and commit or roll back. Disconnect
>    aborts outstanding writes. State errors carry stable diagnostics.
> 2. **Referential integrity.** Foreign-key and check definitions survive catalog restart;
>    `UNIQUE` uses the same unique-index model as compiled schemas. Enforcement runs inside
>    statement brackets and uses the existing MVCC lock manager, including concurrent unique keys.
>
> **Follow-up landed (`d977d34e`).** Phase 4 first enforced foreign keys by locking the transitive
> closure of FK-connected tables exclusively — correct and deadlock-free, but in a normalized schema
> that closure is usually the whole database, so one foreign key serialized nearly every writer.
> Enforcement now takes table-grain intent locks plus a shared lock on the referenced *parent row*,
> matching the hierarchical protocol the row-write path already used. The accepted consequence is
> that wait-for cycles become possible again — the closure's object-id ordering had made them
> impossible — and they surface as the lock manager's existing retryable deadlock abort. A database
> of this shape should detect deadlocks, not avoid them by over-locking.
>
> Neither is a language-only fix: transaction control needs statement-to-session binding in the
> engine, and constraints need catalog persistence, planner awareness, and enforcement on the write
> path. **B2 is therefore an engine feature with a language surface, not parser work** — that is how
> it is scoped and estimated from here.
>
> **Decided 2026-09-17: B2 is promoted ahead of the Graph engine.** A SQL engine that cannot be
> driven transactionally over its own wire protocol is a weaker MVP than a missing fifth model, and
> Graph is the least proven of the three remaining engines. New order: Blob → Documents → B2 → Graph.
>
> **B2's transaction-control scope this pass is `BEGIN` / `COMMIT` / `ROLLBACK` only** — parsed,
> bound to a session-scoped transaction, driving the existing MVCC coordinator. Savepoints and
> isolation-level syntax are **B7**, deferred. They are deferred, *not* ignored: B2's design must
> leave room for both so adding them later is additive rather than a rewrite. Concretely, the
> statement-to-session binding must not assume one flat transaction scope per session (savepoints
> need nested undo scopes), and the session's transaction state must carry an isolation level as a
> value the coordinator already understands rather than hard-coding the default.

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **B7** | **Savepoints and isolation-level syntax** | `SAVEPOINT` / `RELEASE` / `ROLLBACK TO`, and `SET TRANSACTION ISOLATION LEVEL`. The MVCC substrate already models isolation levels, so that half has something real behind it; savepoints need nested undo scopes that do not exist yet. **Deferred, with B2 required to leave room for it.** | `DEFERRED` | #1012 |
| **B3** ✅ | **OQL — the document query language** | Grammar, AST, diagnostics, and a conformance corpus for querying documents. Nothing exists today beyond a 62-line stub. | `OPEN` | #181, #182, #183 |
| **B4** ✅ | **GQL — the graph query language** | Requires choosing the standard first (ISO GQL is the plan's recommendation). Then grammar, AST, diagnostics, conformance plan. | `OPEN` | **#193 (decision)**, #194, #195 |
| ~~**B5**~~ | ~~**A language server per query language**~~ | **Deferred** — editor tooling, not engine capability. An LSP built against grammars still in motion is rework. Revisit once OQL and GQL stabilize. | `DEFERRED` | — |

> **B4 note.** The graph standard is settled: **ISO GQL**. Record the decision on #193 when graph
> work begins.

### Theme C — System catalog and introspection

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **C1** ✅ | **SQL system objects** | Six virtual, database-scoped `INFORMATION_SCHEMA` relations expose tables, columns, keys, checks, and foreign-key actions over the SQL wire protocol. `COHESION_SCHEMA.INDEXES` and `OBJECT_OWNERSHIP` expose index keys and `Adhoc`/`Schema` ownership without adding vendor columns to the ISO-shaped views. Projection, filtering, and ordering use a consistent catalog snapshot; writes and name collisions have a stable read-only diagnostic. See the [MVP view matrix](../../resources/Database/Assimalign.Cohesion.Database.Sql.Language/docs/DIALECT.md#system-view-matrix-c1). | `DONE` | **#1004** |
| **C2** ✅ | **Per-model introspection** | Graph `SHOW` statements expose labels, relationship types, property keys, indexes, and ownership through the shared wire client. Documents OQL exposes virtual `COHESION_SCHEMA.INDEXES` and `OBJECT_OWNERSHIP` collections. Blob adds ownership reads to existing container handles; existing container/blob listings stay intact. Key-Value `KEYSPACES` describes its single implicit keyspace and catalog metadata. Every surface is read-only, computed from the current database's catalog, and preserves the model's ownership/isolation rules without changing existing interfaces. | `DONE` | **#1005** |

### Theme D — Engines

Each engine reaches MVP when it can: create its objects, write and read them durably, survive a
restart with committed data intact, run its query or command surface end to end, and serve a client
over the wire — all through the shared kernel, never re-implementing paging, journaling, or locking.

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **D1** | **SQL engine** | Tables, indexes, transactions, planning, execution, wire server, client. | `DONE` | #178, #179, #180, #912–#914 merged |
| **D2** | **SQL security** | Principals, permissions, and protected-operation checks on relational objects. | `OPEN` | #177 |
| **D3** | **Key-Value engine** | Keyspaces, get/put/delete/scan, transactions, server, client. | `DONE` | #205, #207, #917 merged |
| **D4** | **Key-Value expiration and security** | Per-entry TTL; authorization on key-value operations. | `OPEN` | #919, #206 |
| **D5** ✅ | **Document engine** | Document persistence with versioned metadata, serialization rules for objects/arrays/scalars, secondary indexes, query planning with projection and aggregation, mutation semantics, and a client. **The largest single engine build on this list.** | `OPEN` | #184–#190 |
| **D6** ✅ | **Blob engine** | Chunked large-object persistence, metadata catalog, lifecycle, streaming upload/download. **Engine landed `b97a9976`** (#211, #213). **Wire server and streaming client landed `f9666be8`** (#214). The wire-format decision was made once, for every model, rather than as a side effect of engine work: the per-model protocol split (`2e114097`) gave Blob its own message family for chunked transfer, and the client streams through the shared `IDatabaseStreamingExchange` instead of a columns-and-rows result set — the seam Graph paths later reuse (#1013). | `DONE` | ~~#211~~ ~~#213~~ ~~#214~~ |
| **D7** ✅ | **Graph engine** | Durable adjacency storage, a catalog for labels and edge types, traversal execution, and a typed wire client. Phase 31 adds scalar GQL and mutations over `Execute`, plus real in-process paths served over `ExecutePaths`; catalog reads and engine ownership/scoping semantics are retained. Explicit wire transactions remain deferred. | `DONE` within the declared GQL subset | #196–#200, #202, #1013 |
| **D8** ✅ | **Per-model ownership enforcement** *(Blob containers landed `b97a9976` — first non-SQL proof)* | A1/A2 applied to each engine in its own terms — code-provisioned collections, containers, keyspaces, and graph types locked the same way relational tables are. Implementation varies; a model with no schema may only need the marker. | `DONE` | #1000 |

### Theme E — Durability and operations

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **E1** | **Backup and restore** | Take a consistent backup of a running database and restore it, with version-compatibility checks. Currently the only open kernel gap. | `OPEN` | #161 |
| **E2** ✅ | **Shared MVCC composition** | Both working engines built their own MVCC wiring. Extracting the shared part before three more engines copy it a third, fourth, and fifth time. | `OPEN` | #918 |

> **E2 is sequencing-critical.** It is cheap now and expensive later. Doing it before D5/D6/D7 means
> the new engines consume a shared component instead of each growing their own copy.

### Theme F — Object mapping

Typed access built **on top of each model's client**, so an application works in its own types
instead of rows, documents, or byte streams.

**Three constraints shape this whole theme, and none of them are negotiable:**

1. **It is last by dependency, not by preference.** A mapper sits on a client. The Documents, Graph,
   and Blob clients do not exist yet. F3–F6 cannot start before their engine and client land.
2. **No reflection, so it is source-generated.** The repo mandates `IsAotCompatible=true` and bans
   reflection; every mainstream .NET ORM depends on it, and so does runtime `IQueryable`
   translation. The mappers are emitted at compile time by a Roslyn generator living in
   `analyzers/` (the sanctioned `netstandard2.0` / `IsAotCompatible=false` exception), and the query
   surface is a typed builder, not `IQueryable`.
3. **The schema model is the single source of truth.** Mappers generate from the retained C# schema
   model (`Database.Sql.Schema` after feature A3), never from a second, parallel description of the
   same database. One object model per database or it rots.

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **F1** | **Shared mapping core** | The part that is genuinely the same whatever the store: entity identity, change tracking, unit-of-work with a single `SaveChangesAsync`, and the materialization contracts a generated mapper implements. Ships as `Assimalign.Cohesion.Database.Orm`. | `OPEN` | #1007 |
| **F2** | **Compile-time mapper generation** | A Roslyn generator reads the schema model and your entity types and emits the mapping code — readers, writers, key handling, change-tracking hooks. Nothing is discovered at runtime, so it survives NativeAOT and costs no startup reflection. | `OPEN` | #1007 |
| **F3** | **Relational mapper — SQL** | The real ORM: entity-to-table mapping, primary and foreign keys, relationships, change-tracked inserts/updates/deletes, and a typed query builder that compiles to the SQL the engine already parses. Built on `Database.Sql.Client`. | `OPEN` | #1008 |
| **F4** | **Document mapper — Documents** | An object-document mapper, not a relational one. No joins and no normalization: documents already *are* objects, so the work is identity, serialization shape, and partial update. Built on `Database.Documents.Client`. | `OPEN` | #1009 |
| **F5** | **Graph mapper — Graph** | An object-graph mapper: nodes, edges, and paths materialized from traversal results into typed objects. A different problem from both of the above — traversal results are shaped like paths, not rows. Built on `Database.Graph.Client`. | `OPEN` | #1010 |
| **F6** | **Typed accessors — Key-Value and Blob** | **Deliberately not an ORM.** Key-Value gets a typed serializer over a key convention; Blob gets typed metadata over blob properties plus typed stream access. Named honestly so nobody expects querying from a store that cannot query. | `OPEN` | #1011 |

> **On the name.** "ORM" is relational vocabulary. Across five models the accurate term is a data
> mapper family, and the package names say what each one actually does. Keeping "ORM" only where it
> is true — F3 — avoids promising relational semantics on stores that have none.

---

## 3. Rules, as I will enforce them

Your rules, restated as things a build or a review can check.

| | Rule | How it is enforced |
|---|---|---|
| **R1** | No server-level language or command API. Everything scopes to a database. | Feature A5. Conformance test per model; connection binds one database at handshake. |
| **R2** | Languages are per-model opt-in, not one flat grammar. | Feature B1. Clause capability profile in the shared language package. |
| **R3** | Relational provisioning does not live in the area root. | Feature A3 + A4. Enforced by reference rules and documented in the area README. |
| **R4** | No application-model or hosting-resource wiring this pass. | Nothing in Theme A–E touches `Database.Hosting` or `Database.ApplicationModel`. #973, #167, #168 stay open. |
| **R5** | Code-provisioned objects are locked against out-of-band change. Applies to every model; implementation varies. | Features A1, A2, D8. |
| **R7** | Migrations wait until the engines run. | #857, #858, #859, #176 stay open and untouched. |
| **R8** | Abstraction changes on database interfaces get reviewed with you, not absorbed silently. | See §5 — I am proposing we freeze the contracts up front. |
| **R9** | SQL gets a base design for system objects. | Feature C1. |

---

## 4. Explicitly out of scope this pass

Not forgotten — deferred by your rules 4 and 7, and by prior program decisions.

- **Hosting and application-model wiring** — #973, #166, #167, #168. Engines get built; wiring them
  into the host comes after.
- **Migration engine and database projects** — #857, #858, #859, #176.
- **Replication, every model** — #192, #203, #204, #215, #216. Already post-MVP in the plan.
- **Cache model** — #208, #209, #210. Already post-MVP behind key-value.
- **Encryption at rest** — #861.
- **Embedded engine consumption** — #862.

---

## 5. Decisions — settled 2026-09-17

| | Question | Decision |
|---|---|---|
| **Q1** | Graph query standard (#193) — never decided, gates every graph item. | **ISO GQL.** The ratified standard, as the program plan recommended. #193 is hereby answered; record it on the issue when graph work starts. Graph stays in this pass. |
| **Q2** | Rule 8 (review abstraction changes together) versus one autonomous run. | **Freeze the contracts first.** Before any engine code, one reviewable diff carries every interface change: root abstractions, the ownership/locking contract, the language capability profile, and the minimal root contracts for the three new engines. Signed off once. Implementation then runs against frozen contracts — **anything that needs a new interface member halts and comes back**, rather than being invented to bridge an implementation. |
| **Q3** | Is B5 (language servers) in the MVP? | **Deferred entirely.** Editor tooling, not engine capability, and building an LSP against grammars still in motion is rework. Revisit once OQL and GQL stabilize. B5 is struck from this pass. |
| **Q4** | Base branch — `dev/dx-design-buildout` and `origin/development` have diverged (136 / 342 commits). | **Branch from `dev/dx-design-buildout`**, as `feature/L03.02-mvp-engines`. It carries the DX realignment the Database area was just moved onto. |

### Sequencing that follows from these

1. **Phase 1 — contract freeze.** Themes A1–A5, B1, and the root contracts for Documents, Blob,
   and Graph. One diff, reviewed before implementation starts.
2. **Phase 2 — Blob engine (D6).** Simplest of the three; proves the ownership and introspection
   pattern end to end with no query language in the way.
3. **Phase 3 — Document engine (D5) + OQL (B3).** The largest build.
4. **Phase 4 — SQL transaction control and referential integrity (B2).** *Promoted ahead of Graph
   on 2026-09-17*, once B1 measured the real language gap. `BEGIN`/`COMMIT`/`ROLLBACK` bound to
   session-scoped transactions, plus foreign keys, check and unique constraints with catalog
   persistence, planner awareness, and write-path enforcement. Design must leave room for **B7**.
5. **Phase 5 — Graph engine (D7) + GQL (B4).** Unblocked by the ISO GQL decision.
6. **Phase 6 — fills.** SQL system objects (C1), SQL security (D2), key-value TTL and security
   (D4), backup/restore (E1), remaining SQL clauses (set operations, CTEs, views, window functions).
7. **Phase 7 — object mapping (Theme F).** Last, because every mapper sits on a client that Phases
   2–5 create. Order within it: F1 and F2 (core + generator) → F3 (SQL, the one true ORM and the
   proving ground for the generator) → F4, F5, F6.

**E2 (shared MVCC extraction, #918) lands inside Phase 1**, before three new engines each grow
their own copy of the wiring SQL and key-value both wrote independently.

Commits land at every phase boundary so there is something reviewable before all three engines
arrive together.
