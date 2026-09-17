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
| **SQL** | ~14,300 lines across engine, language, catalog, client, storage | ~6,900 | **Working.** Parses, plans, executes, indexes, serves over the wire, MVCC-correct. |
| **Key-Value** | ~6,500 lines across engine, client, catalog, storage | ~2,700 | **Working.** Storage, commands, server, client all landed. |
| **Documents** | ~500 lines — root contracts, a 62-line language stub, a 150-line storage stub | ~780 | **Not built.** No parser, no planner, no engine. |
| **Graph** | ~350 lines — root contracts and an 86-line language stub | ~6 | **Not built.** Also blocked: the query standard was never chosen. |
| **Blob** | ~260 lines — root contracts and a 6-line storage stub | ~5 | **Not built.** |
| **Cache** | 6 lines | 6 | Out of MVP scope by prior decision. |

**Shared kernel — all landed and in use by both working engines:** durable page store with CRC and
crash recovery, write-ahead journal, MVCC with snapshot isolation and deadlock detection, B+Tree
secondary indexes, the shared type system with order-preserving encodings, the execution pipeline,
and the wire protocol with a pooled client.

**The honest summary:** two of five engines work. The kernel they stand on is solid and proven by
two independent consumers. The remaining three engines are greenfield, and the work to bring them
up is comparable in size to everything already built in this area.

### What is open in GitHub

| Area | Open items | Note |
|---|---|---|
| Documents | #181–#192 + 7 epics | Entire model, language through client |
| Graph | #193–#204 + 7 epics | #193 (standard selection) gates every other graph item |
| Blob | #211–#216 + 4 epics | Entire model |
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
| **A1** | **Every database object knows who created it** | A table created by your C# schema code is marked as code-owned. A table created by running `CREATE TABLE` is marked as SQL-owned. The engine records the difference and persists it in the catalog. | `NEW` | file new |
| **A2** | **Code-owned objects cannot be altered or dropped by ad-hoc statements** | If your application provisions a `Customers` table from C#, then `DROP TABLE Customers` over a SQL connection is refused with a clear error naming the owning schema. Only a schema deployment can change it. Objects created by plain SQL stay fully mutable by plain SQL. | `NEW` | file new |
| **A3** | **Schema provisioning moves out of the shared root into the SQL model** | Today the area root carries `IDatabaseSchemaTable`, `IDatabaseSchemaColumn`, triggers, functions, grants and a migration planner that switches on engine model. Relational vocabulary in a model-agnostic root is the violation you flagged. It moves to the SQL model; each other model gets its own provisioning shape, or none. | `NEW` | file new |
| **A4** | **The multi-root structure is written down** | Documentation stating that `Assimalign.Cohesion.Database` is the area root, that `*.Sql.*`, `*.Blob.*`, `*.Documents.*`, `*.Graph.*`, `*.KeyValuePair.*` are model families inheriting it, and what may live in which. Prevents the next session repeating A3. | `NEW` | file new |
| **A5** | **No server-scoped query language, anywhere** | A connection binds to exactly one database and cannot address another. Already true at the wire protocol — the startup handshake takes a database name. This feature makes it a *guarded* property: a conformance test per model proving no statement or command can reach across databases or reach the server. | `NEW` | file new |

> **A5 note.** Creating and dropping databases stays on `IDatabaseEngine` in C#. That is host-side
> composition — the code that owns the engine process — not a client-facing API, and no client or
> language surface exposes it. If you want that removed too, say so; it is a larger change.

### Theme B — Query language platform

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **B1** | **Each model opts into the clauses it supports** | The shared language package today hands every model the same lexer and a flat keyword list. B1 adds a capability profile: a model declares which clauses it accepts, and anything outside the profile produces a precise "not supported by this model" diagnostic instead of a generic parse failure. | `NEW` | file new |
| **B2** | **A published, complete SQL surface** | The declared dialect gets scoped to a stated MVP line and filled in: joins, subqueries, `CASE`, set operations, `GROUP BY`/`HAVING`, window basics, CTEs, `LIMIT`/`OFFSET`, plus a documented builtin-function set. Today keywords are recognized ahead of parser support, so some accepted tokens do nothing. | `PARTIAL` | #172, #173, #174 |
| **B3** | **OQL — the document query language** | Grammar, AST, diagnostics, and a conformance corpus for querying documents. Nothing exists today beyond a 62-line stub. | `OPEN` | #181, #182, #183 |
| **B4** | **GQL — the graph query language** | Requires choosing the standard first (ISO GQL is the plan's recommendation). Then grammar, AST, diagnostics, conformance plan. | `OPEN` | **#193 (decision)**, #194, #195 |
| ~~**B5**~~ | ~~**A language server per query language**~~ | **Deferred** — editor tooling, not engine capability. An LSP built against grammars still in motion is rework. Revisit once OQL and GQL stabilize. | `DEFERRED` | — |

> **B4 note.** The graph standard is settled: **ISO GQL**. Record the decision on #193 when graph
> work begins.

### Theme C — System catalog and introspection

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **C1** | **SQL system objects** | Queryable metadata built into the engine: what tables exist, their columns and types, indexes, constraints, and — tied to A1 — which objects are code-owned versus SQL-owned. Standard-shaped (`INFORMATION_SCHEMA`) so existing tooling recognizes it. | `NEW` | file new |
| **C2** | **Per-model introspection** | The equivalent for the other models, in each model's own vocabulary: collections and indexes for documents, containers and blobs for blob, keyspaces for key-value, labels and edge types for graph. Scoped per model — a model without a schema gets a smaller surface. | `NEW` | file new |

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
| **D5** | **Document engine** | Document persistence with versioned metadata, serialization rules for objects/arrays/scalars, secondary indexes, query planning with projection and aggregation, mutation semantics, and a client. **The largest single engine build on this list.** | `OPEN` | #184–#190 |
| **D6** | **Blob engine** | Chunked large-object persistence, a metadata catalog, lifecycle operations, upload/download streaming, and a client. Simplest of the three — no query language. | `OPEN` | #211, #213, #214 |
| **D7** | **Graph engine** | Durable adjacency storage, a catalog for labels and edge types, traversal execution, and a client. Gated on B4, which is gated on the #193 decision. | `OPEN` | #196–#200, #202 |
| **D8** | **Per-model ownership enforcement** | A1/A2 applied to each engine in its own terms — code-provisioned collections, containers, keyspaces, and graph types locked the same way relational tables are. Implementation varies; a model with no schema may only need the marker. | `NEW` | file new |

### Theme E — Durability and operations

| # | Feature | What it means | Status | Work items |
|---|---|---|---|---|
| **E1** | **Backup and restore** | Take a consistent backup of a running database and restore it, with version-compatibility checks. Currently the only open kernel gap. | `OPEN` | #161 |
| **E2** | **Shared MVCC composition** | Both working engines built their own MVCC wiring. Extracting the shared part before three more engines copy it a third, fourth, and fifth time. | `OPEN` | #918 |

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
| **F1** | **Shared mapping core** | The part that is genuinely the same whatever the store: entity identity, change tracking, unit-of-work with a single `SaveChangesAsync`, and the materialization contracts a generated mapper implements. Ships as `Assimalign.Cohesion.Database.Orm`. | `NEW` | file new |
| **F2** | **Compile-time mapper generation** | A Roslyn generator reads the schema model and your entity types and emits the mapping code — readers, writers, key handling, change-tracking hooks. Nothing is discovered at runtime, so it survives NativeAOT and costs no startup reflection. | `NEW` | file new |
| **F3** | **Relational mapper — SQL** | The real ORM: entity-to-table mapping, primary and foreign keys, relationships, change-tracked inserts/updates/deletes, and a typed query builder that compiles to the SQL the engine already parses. Built on `Database.Sql.Client`. | `NEW` | file new |
| **F4** | **Document mapper — Documents** | An object-document mapper, not a relational one. No joins and no normalization: documents already *are* objects, so the work is identity, serialization shape, and partial update. Built on `Database.Documents.Client`. | `NEW` | file new |
| **F5** | **Graph mapper — Graph** | An object-graph mapper: nodes, edges, and paths materialized from traversal results into typed objects. A different problem from both of the above — traversal results are shaped like paths, not rows. Built on `Database.Graph.Client`. | `NEW` | file new |
| **F6** | **Typed accessors — Key-Value and Blob** | **Deliberately not an ORM.** Key-Value gets a typed serializer over a key convention; Blob gets typed metadata over blob properties plus typed stream access. Named honestly so nobody expects querying from a store that cannot query. | `NEW` | file new |

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
4. **Phase 4 — Graph engine (D7) + GQL (B4).** Now unblocked by the ISO GQL decision.
5. **Phase 5 — fills.** SQL system objects (C1), SQL dialect completion (B2), SQL security (D2),
   key-value TTL and security (D4), backup/restore (E1).
6. **Phase 6 — object mapping (Theme F).** Last, because every mapper sits on a client that Phases
   2–4 create. Order within it: F1 and F2 (core + generator) → F3 (SQL, the one true ORM and the
   proving ground for the generator) → F4, F5, F6.

**E2 (shared MVCC extraction, #918) lands inside Phase 1**, before three new engines each grow
their own copy of the wiring SQL and key-value both wrote independently.

Commits land at every phase boundary so there is something reviewable before all three engines
arrive together.
