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
| **SQL** | ~14,300 lines at B1 baseline; B2 extends engine, language, catalog and schema | ~6,900 at B1 baseline; B2 adds acceptance coverage | **Working, with transaction control and referential integrity.** B2 raises parser/profile coverage from **21 to 32 of 48 declared clauses**: SQL transactions run through the wire server, and durable foreign keys, checks and unique indexes enforce writes. Set operations, CTEs and the remaining 16 clauses are deferred; parser coverage is not a claim that every parsed query shape executes. See feature B2. |
| **Key-Value** | ~6,500 lines across engine, client, catalog, storage | ~2,700 | **Working.** Storage, commands, server, client all landed. |
| **Documents** | engine, OQL parser, planner, chunked storage, catalog | 276 | **Working** *(landed `6085bad3`, `a4770b3f`)*. OQL with 8 executable clauses including `CREATE INDEX` / `DROP INDEX` DDL, planner index selection, nested documents, arrays, mixed-shape collections, collection ownership. No wire client. |
| **Graph** | engine, GQL parser, traversal planner, adjacency storage, catalog | 189 | **Working** *(landed `1092d6b2`)*. ISO/IEC 39075 GQL with 7 executable clauses, relationship-isomorphic cycle termination, indexed multi-hop traversal, `DETACH DELETE`, label/type ownership. No wire client. |
| **Blob** | engine, chunked storage, catalog | 39 | **Working** *(landed `b97a9976`)*. Chunked persistence, atomic publication, streaming reads/writes proven at 128 MiB under a 64 MiB heap, crash-durable, container ownership enforced. No wire client — see #214. |
| **Cache** | 6 lines | 6 | Out of MVP scope by prior decision. |

**Shared kernel — landed and in use by all five engines:** durable page store with CRC and crash
recovery, write-ahead journal, MVCC with snapshot isolation and deadlock detection, the shared
per-database MVCC composition (extracted in `31047f3a` before three engines could each grow their
own copy), B+Tree secondary indexes, the shared type system with order-preserving encodings, the
execution pipeline, and the wire protocol with a pooled client.

**The honest summary: all five engines work.** Each composes the kernel rather than
re-implementing paging, journaling, or locking, and each is crash-durable, ownership-enforcing, and
scoped to a single database. Blob forced the kernel to grow large-object support it had been
missing; nothing after it needed a kernel change.

**What separates this from a usable platform** is one decision, not five: **three of the five
engines have no wire server.** Documents, Blob, and Graph are in-process only, all blocked behind
the same protocol limits — frames cap at 16 MiB, results are column/row shaped, and the startup
handshake carries no model discriminator. That is a single coherent wire-format revision, and
deciding the three cases separately risks three incompatible extensions to `ProtocolMessageType`.
See feature D6's note and §3.4 of `docs/resources/Database/DESIGN.md`.

### What is open in GitHub

| Area | Open items | Note |
|---|---|---|
| Documents | #190–#192 + epics | #181–#189 close on merge (`6085bad3`). Client, security, replication remain |
| Graph | #201–#204 + epics | #193–#200 close on merge (`1092d6b2`); **#193 answered: ISO/IEC 39075 GQL**. Security, client, replication remain |
| Blob | #212, #214–#216 + epics | Engine landed (#211, #213 closed); client blocked on the wire-format decision |
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
| **B2** ✅ | **A published, complete SQL surface** | Phase 4 adds wire-accessible `BEGIN` / `COMMIT` / `ROLLBACK`, durable foreign keys with delete cascade/restrict, row checks and unique indexes with concurrent-write enforcement. Coverage rises from **21/48 to 32/48**; the remaining language groups below keep the broader published-surface feature partial. DDL remains self-committing and is refused inside explicit transactions. | `PARTIAL` (Phase 4 transaction/constraint slice implemented) | #172, #173, #174; catalog constraint portions of #175 / #177 |

> **What the SQL surface actually supports (measured 2026-09-17, after B2).**
>
> **Supported (32; previously 21 of 48):** `SELECT` `INSERT` `UPDATE` `DELETE` `CREATE TABLE` `CREATE INDEX`
> `ALTER TABLE` `DROP TABLE` `DROP INDEX` `FROM` `JOIN` `WHERE` `GROUP BY` `HAVING` `ORDER BY`
> `LIMIT` `OFFSET` `VALUES` subqueries `CASE` `CAST` `BEGIN` `COMMIT` `ROLLBACK`
> `TRANSACTION` `FOREIGN KEY` `REFERENCES` `CHECK` `UNIQUE` constraint `CONSTRAINT`
> `CASCADE` `RESTRICT`. The eleven additions have engine enforcement; some older
> parser-supported query shapes remain outside the planner's execution surface.
>
> **Not implemented (16):**
>
> | Group | Missing |
> |---|---|
> | **Set operations** | `UNION` · `INTERSECT` · `EXCEPT` |
> | **CTEs** | `WITH` · `RECURSIVE` |
> | **Window functions** | `OVER` · `PARTITION BY` · `WINDOW` |
> | **Views** | `CREATE VIEW` · `DROP VIEW` |
> | **Joins** | `NATURAL` · `USING` |
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
| **C1** | **SQL system objects** | Queryable metadata built into the engine: what tables exist, their columns and types, indexes, constraints, and — tied to A1 — which objects are code-owned versus SQL-owned. Standard-shaped (`INFORMATION_SCHEMA`) so existing tooling recognizes it. | `OPEN` | **#1004** |
| **C2** | **Per-model introspection** | The equivalent for the other models, in each model's own vocabulary: collections and indexes for documents, containers and blobs for blob, keyspaces for key-value, labels and edge types for graph. Scoped per model — a model without a schema gets a smaller surface. | `OPEN` | #1005 |

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
| **D6** ✅ | **Blob engine** | Chunked large-object persistence, metadata catalog, lifecycle, streaming upload/download. **Engine landed `b97a9976`** (#211, #213). The **client (#214) is blocked on a wire-format decision**: the protocol caps a frame at 16 MiB and models results as columns and rows, so streaming needs either new `ProtocolMessageType` entries for chunked transfer or a separate channel. That choice affects every model's client, so it is not being made as a side effect of engine work. | `PARTIAL` | ~~#211~~ ~~#213~~ · #214 blocked |
| **D7** ✅ | **Graph engine** | Durable adjacency storage, a catalog for labels and edge types, traversal execution, and a client. Gated on B4, which is gated on the #193 decision. | `OPEN` | #196–#200, #202 |
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
