# Cohesion Data Platform — Area Design

**Status:** scaffold-complete, engines in build-out · **Owner:** Chase Crawford · **Scope:** everything under `resources/Database/`, plus the `Assimalign.Cohesion.Sdk.Database` build tooling under `sdks/` and the `App.Database` framework family.

This document is the durable architecture record for the multi-model OLTP database engine. It captures the requirements, the project decomposition, the dependency directions, and the reasoning behind them. Sequencing and work-item tracking live in [docs/DATABASE_PROGRAM_PLAN.md](../../docs/DATABASE_PROGRAM_PLAN.md) (temporary program scaffolding); this file is what survives after the backlog drains.

---

## 1. What we are building

A **multi-model OLTP database platform**: five independent database engines — **SQL**, **Documents**, **Graph**, **Blob**, and **KeyValuePair** — that share one durable kernel (storage, write-ahead logging, transactions, indexing) while keeping model semantics explicit and separate. Inspirations: PostgreSQL (relational engine discipline, MVCC, WAL), RavenDB (document model, embedded + server duality), Neo4j (native graph storage and traversal).

The platform runs in two modes, matching Cohesion's identity:

- **Out-of-process:** a standalone database server hosted by `Database.Hosting` (a `Host<TContext>` composition), fronted by the wire protocol server, orchestrated as a resource through the ApplicationModel.
- **In-process (embedded):** an engine linked directly into an application process — no server, no wire protocol, same engines, same ACID guarantees. `Database.Embedded` is the facade for this mode, and it is a first-class deliverable: the Database area is the **data layer for the rest of the Cohesion platform** (R10) — resources like ConfigurationStore, SecretStore, Scheduler, and the hubs embed engines for their own state rather than depending on a separate database service.

## 2. Requirements

### 2.1 Platform requirements (all models)

| # | Requirement | Where it lands |
|---|---|---|
| R1 | **ACID compliance for every model.** Atomicity and durability via WAL + recovery; isolation via MVCC snapshots with pessimistic locks where required; consistency via per-model constraint enforcement. | `Database.Storage` (WAL, recovery), `Database.Transactions` (MVCC, locks, isolation levels), per-model catalogs (constraints) |
| R2 | **Every model has its own independent engine.** One `IDatabaseEngine` implementation per model (`SqlDatabaseEngine`, `DocumentDatabaseEngine`, `GraphDatabaseEngine`, `BlobDatabaseEngine`, `KeyValueDatabaseEngine`). Engines compose kernel subsystems internally; no engine depends on another engine. | `Database.{Model}` root projects |
| R3 | **Every model builds on the shared kernel.** Storage, journaling, transactions, and indexing are reused, never duplicated per model. A model may bring a model-specific storage *layout* (e.g. graph adjacency pages) but it lives on the shared page/WAL substrate. *(Assumption: the original requirement statement was cut short — "every model should utilize …" — and has been read as "…the shared core engine", consistent with the existing backlog language and issue #31. Flag if wrong.)* | kernel projects (§4.1) |
| R4 | **Hosting via `libraries/Hosting`.** The database host is a `Host<DatabaseApplicationContext>` whose composed services follow the per-service execution menu: each wire-protocol endpoint on `BackgroundService` (async accept loop). No second lifecycle model. The Lane-H dedicated-thread guardrail (WAL flush and page write-back on dedicated threads, immune to pool starvation) is satisfied *inside the engine* — the engine spawns and owns those threads for its whole life (2026-07-13; engines are data machines, see §3.5). | `Database.Hosting` (endpoint), engines (durability threads) |
| R5 | **Orchestration via the ApplicationModel.** A manifest-only `Database.ApplicationModel` project declares the resource (`IExecutableResource` + `IEndpointResource` + `IMountResource`) and provides `AddDatabase(...)`. It never references the runtime; the runtime never references it. | `Database.ApplicationModel` |
| R6 | **NativeAOT throughout.** No reflection-based serialization, no runtime codegen, no plugin discovery via `Assembly.LoadFrom`. Model engines are composed statically. | all projects (`IsAotCompatible=true`) |
| R10 | **The platform data layer.** Every other Cohesion resource can use this area as its data layer. That mandates the **engine self-sufficiency principle**: an engine owns its internal background workers (WAL flush, checkpoint, version pruning) whether embedded or hosted, so `Database.Hosting` is composition-only and `Database.Embedded` is thin. Cross-resource packaging rides the `CohesionPrivateProjectReference` + `CohesionFrameworkPrivateAssembly` pattern when a resource hides its data layer from consumers. | `Database.Embedded` (facade, shipped), every engine (#862) |
| R11 | **Enterprise readiness.** Beyond correctness: encryption at rest keyed by the platform key ring (#861, consuming `Security.DataProtection` #774), per-model authorization (#177 …), quotas/tenancy/audit (#167), backup/restore with version compatibility (#161, PITR via WAL archiving as the follow-on), health/readiness/diagnostics (#168). Security and governance run in parallel with engine work, not after it (roadmap feature-ordering rule). | kernel + governance features |

### 2.2 Tooling requirements (Database SDK)

| # | Requirement | Where it lands |
|---|---|---|
| R7 | **Database projects.** A consumer creates a *database project* (`<Project Sdk="Assimalign.Cohesion.Sdk.Database">`) holding declarative schema sources. The build validates and compiles the schema into a deployable artifact. (Shape chosen: the DacFx/SSDT-style declarative experience — schema-as-source, build-time validation, artifact output, migration generation by diffing.) | `Sdk.Database` targets + Tasks |
| R8 | **Migrations as a build tool.** The SQL model gets a migration tool implemented in the Database SDK: diff the compiled schema model against a baseline (or a live catalog), emit ordered migration scripts, and apply them transactionally with rollback and compatibility checks. | `Sdk.Database` Tasks (`Assimalign.Cohesion.Sdk.Database.Migration.targets`), runtime apply engine in `Database.Sql.Catalog` |
| R9 | **Model-specific build tools, loaded by model.** The SDK inspects `$(CohesionDatabaseModel)` on the consumer project and imports the matching per-model targets (`Sdk.Database.Sql.targets`, `Sdk.Database.Documents.targets`, …). SQL ships first; other models get index/schema artifact compilation later. Static MSBuild imports — no runtime plugin loading — keeps this AOT-clean. | `sdks/Assimalign.Cohesion.Sdk.Database/Targets/` |

### 2.3 Explicit non-goals (for the MVP)

- **No distributed consensus / sharding.** Replication contracts exist (`Database.Replication`) and single-leader log shipping is the post-MVP path; Raft-style clustering is out of scope until the engines are durable and correct on one node.
- **No cross-model queries.** Each engine owns its language and its data. Multi-model joins are a product decision for later, not an engine seam to pre-build.
- **Cache model is not in the MVP.** The `Database.Cache.*` projects remain (coherence/eviction is a real, distinct model) but all Cache work is deferred behind KeyValuePair.
- **No query optimizer sophistication.** MVP planners are rule-based (predicate pushdown, index selection); cost-based optimization is a later feature with its own epic.

## 3. Architecture

### 3.1 Layering

```
┌─────────────────────────────────────────────────────────────────────┐
│ Orchestration      Database.ApplicationModel (manifest-only)        │
├─────────────────────────────────────────────────────────────────────┤
│ Hosting            Database.Hosting (Host<TContext> composition,    │
│                    DI/Config/Logging seam — the ONLY DI seam;       │
│                    composition-only: wraps IDatabaseServer instances│
│                    as endpoint host services)                       │
├─────────────────────────────────────────────────────────────────────┤
│ Service surface    Database.Server (shared server base: sessions,   │
│                    guardrails, drain — per-model servers derive)    │
│                    Database.Client · Database.Replication           │
├─────────────────────────────────────────────────────────────────────┤
│ Model engines      Sql · Documents · Graph · Blob · KeyValuePair    │
│  (per model)       {Model} (engine + {Model}DatabaseServer)         │
│                    {Model}.Language · {Model}.Storage               │
│                    {Model}.Catalog · {Model}.Client · {Model}.Security
│                    {Model}.Replication                              │
├─────────────────────────────────────────────────────────────────────┤
│ Kernel (shared)    Database (contracts; rolls up the child roots)   │
│                    Database.Execution · Database.Transactions       │
│                    Database.Indexing · Database.Storage (pages,     │
│                    buffer pool, WAL, recovery) · Database.Types     │
│                    Database.Language · Database.Protocol            │
│                    Database.Security · Database.Governance          │
└─────────────────────────────────────────────────────────────────────┘
```

Dependency direction is strictly downward. Model engines depend on kernel projects; kernel projects never depend on a model. The service surface depends on the root contracts (`Database`, `Database.Execution`) and is model-agnostic: the server dispatches to whichever engines the host composed.

**The root rolls up the child roots** (2026-07-13 inversion — decision log). `Assimalign.Cohesion.Database` references its nine child roots — `Types`, `Language`, `Storage`, `Transactions`, `Execution`, `Indexing`, `Protocol`, `Security`, `Governance` — and **no child root references the root**. A database has a vast base-component surface; the child roots break it out for separation of concerns and testability, and stay independently consumable precisely because the arrow points root → child. Child-to-child references are fine (`Transactions → Storage`, `Execution → Language/Types`); each child owns its own vocabulary (`ProtocolVersion` in `Protocol`, `TransactionId`/`TransactionState` in `Transactions`) and its own exception root (`StorageException`, `ProtocolException`, `TransactionAbortedException`, `IndexException`, … inherit `Exception` directly — `DatabaseException` covers the root and everything built *above* it). `Database.Indexing` joined the child roots on 2026-07-13 (owner direction) once its only root coupling — `DatabaseException` ancestry — was re-rooted onto its own `IndexException`.

### 3.2 The kernel

- **`Database`** — the public contract root: `IDatabase`, `IDatabaseEngine`, `IDatabaseSession`, `IDatabaseTransaction`, `DatabaseException` (the exception root for the contracts and everything above them), `DatabaseName`, lifecycle enums. Model-agnostic; every engine, feature library, and service-surface project references it, and it in turn rolls up the child roots (so `TransactionId`, `ProtocolVersion`, and the rest of the base vocabulary arrive transitively).
- **`Database.Storage`** — the physical layer: slotted pages, buffer pool with pin/evict, free-space map, journal (WAL) streams, backup/recovery seams. Owns `PageId`, `JournalRecord`, CRC integrity. The WAL contract is ARIES-shaped: append redo/undo records under an LSN discipline, checkpoint, replay on open. Durability policy (fsync cadence, group commit) is an engine-level option surfaced through storage.
- **`Database.Transactions`** — the ACID heart (new): `ITransactionManager` (begin/commit/rollback under an `IsolationLevel`), `TransactionSnapshot` (MVCC visibility: xmin/xmax/active-set), `ILockManager` (shared/update/exclusive + intent modes, deadlock detection), `ITransactionLog` (the seam that binds transaction lifecycle to the WAL). Engines *use* the manager; sessions *expose* the resulting `IDatabaseTransaction`.
- **`Database.Indexing`** — shared index infrastructure (new): order-preserving byte-comparable `IndexKey` encoding, `IIndex` (point/range search, insert/delete), `IIndexCursor` streaming iteration, B+Tree first and hash later, built on shared pages so index updates ride the same WAL/transaction path as data. SQL secondary indexes, document indexes, graph adjacency lookups, and the KV primary structure are all consumers.
- **`Database.Types`** — the shared scalar type system: type identity, comparison/collation, binary encoding. Anything that ends up inside an `IndexKey` or a stored value goes through here so ordering is consistent across models.
- **`Database.Execution`** — model-agnostic execution contracts: `QueryRequest`/`QueryResult` families, result streaming, and (build-out) the plan/operator seam per-model planners implement.
- **`Database.Language`** — shared lexer/parser/diagnostics infrastructure the per-model languages build on.

### 3.3 Model engines

Each model root project owns a public engine (`{Model}DatabaseEngine`, static `Create(options)` factory, `IDatabaseEngine` implementation) and the model's public database interface:

| Model | Interface | Shape | Language |
|---|---|---|---|
| SQL | `ISqlDatabase` | tables/schemas/views over row-oriented slotted pages | SQL dialect (declared matrix, conformance corpus) — `Sql.Language` |
| Documents | `IDocumentDatabase` | named collections of versioned documents (`IDocumentCollection`) | OQL-based contract — `Documents.Language` |
| Graph | `IGraphDatabase` | property graph: nodes, typed directed relationships, traversal | standard TBD (ISO GQL is the recommended default; decision gates deep language work) — `Graph.Language` |
| Blob | `IBlobDatabase` | containers of streamed large objects + metadata catalog | none (API-driven) |
| KeyValuePair | `IKeyValueDatabase` | ordered key space, point/range ops, TTL | none for MVP (commands ride the wire protocol directly) |

Per-model satellite projects follow one matrix: `.Language` (where a language exists), `.Storage` (model-specific layouts on the shared substrate), `.Catalog` (schema/metadata, constraint enforcement, migration apply for SQL), `.Client` (typed client over the shared client core), `.Security` (model-specific authorization), `.Replication` (model-specific replication semantics on the shared contracts).

**Layout verdict:** the pre-existing matrix layout is kept. It is granular, but the granularity maps 1:1 to the backlog structure and keeps per-model concerns out of the kernel. Deliberate asymmetries: Blob and KeyValuePair have no `.Language` project (Blob is API-driven; KV commands are protocol verbs); `Sql.Replication` was added for parity; `Database.Memory` remains the in-memory storage strategy used by tests and embedded scenarios; `Database.Embedded` is the in-process consumption facade for the platform data layer (R10).

### 3.4 Service surface

- **`Database.Protocol`** (new) — the wire protocol shared by server and client: length-prefixed, big-endian frame header (`u32 length + u8 type`), message families for startup/auth, query execute (parse/bind/execute), streaming result sets (header/row/complete), transaction control, and errors. Versioned handshake so protocol evolution never breaks deployed clients. Pure value objects + reader/writer contracts; no sockets here.
- **`Database.Server` + the per-model servers** — the network front-end. `Database.Server` is shared server infrastructure above the root: the guided abstract base (`DatabaseServer`) implementing everything model-agnostic — accept connections (via `libraries/Connections` drivers), authenticate a session principal, bind the session to a database, pump protocol frames into `IDatabaseSession` executions, drain in two phases. **Servers are per-model** (2026-07-13): each model ships `{Model}DatabaseServer : DatabaseServer` fronting exactly one engine (`SqlDatabaseServer` first), which is where model-specific wire behavior grows. "Running" lives on the server; engines beneath it are data machines with no lifecycle. (History: the original `Database.Server` project was folded into `Database.Hosting` on 2026-07-12, then resurrected as this shared base on 2026-07-13 when per-model servers made a hosting-resident base underivable — see the decision log.)
- **`Database.Client`** — the shared client core: connection strings, connection pooling, protocol client, result materialization. Per-model `.Client` projects add typed surfaces on top.
- **`Database.Security`** — authn/authz contracts (principals, roles, permission checks) consumed by the server and per-model security projects.
- **`Database.Replication` / `Database.Governance`** — shared replication contracts (log shipping seam over the WAL) and operational governance (quotas, tenancy, audit events). Post-MVP build-out; contracts stay in place so model services don't invent local equivalents.

### 3.5 Hosting and orchestration

`Database.Hosting` is **composition-only** (2026-07-13 redesign): it wraps composed `IDatabaseServer` instances generically as endpoint host services (started last, drained first), runs any additional composition-root services, and implements the root's application builder — its references are the area root plus the non-area `Hosting` foundation, nothing else. The server machinery lives in `Database.Server` (the shared base per-model servers derive from — see §3.4); the `IDatabaseServer`/`IDatabaseServerContext`/`IDatabaseServerSession` abstractions live in the area root so feature libraries can consume the seam, the Web area's shape. Composition is **builder-first** (the cross-area builder pattern): the root's `IDatabaseApplicationBuilder` is the seam model packages register their engines *and servers* on — `Database.Sql` ships `AddSqlDatabase(...)` and `AddSqlServer(...)`, and every model ships its own verbs — while `Database.Hosting` implements the builder and provides the entry point (`DatabaseApplication.CreateBuilder()`). It is the only project that touches DI/Configuration/Logging (repo rule: `*.Hosting` is the only DI seam). Per the engine self-sufficiency principle (R10) taken to its conclusion, engines own their background work loops **unconditionally** — spawned at engine creation on engine-owned dedicated threads, quiesced on dispose — so the host schedules nothing for them (the 2026-07-12 claim-handshake model was deleted; see the decision log). `Database.ApplicationModel` is manifest-only: `DatabaseResource` declares artifact `Assimalign.Cohesion.Database.Application`, one declared endpoint (`db` / scheme `cohesion-db`, platform-allocated port by default), and a persistent data volume mount; `AddDatabase(...)` composes it into an application graph. The artifact itself is the `Assimalign.Cohesion.Database.Application` **executable** (#904): the composition-root process that binds the conventional environment variables, composes a file-backed SQL engine + TCP endpoint + `DatabaseApplication` through the builder, and drains gracefully on SIGTERM — it carries the sanctioned per-project COHRES001 exemption (an executable is the analog of a user application) and deliberately ships outside the `App.Database` framework (frameworks deliver libraries). **The executable is the interim state (pinned, #906):** per the `<Area>.Application` convention (`.claude/rules/resource-areas.md`), the project's target design is the manifest-generation project SDK consumers load — build tasks code-gen the application manifest, and `Database.ApplicationModel` surfaces it to the gateway; the realignment waits on the ApplicationModel program. When the repo-wide `.Hosting` → `.Application` rename lands (ApplicationModel DESIGN.md Phase 3), Database follows.

### 3.6 ACID, concretely

- **Atomicity** — all writes stage through the transaction's WAL records; commit is a single durable WAL commit record; rollback replays undo.
- **Consistency** — per-model catalogs enforce constraints (SQL: PK/unique/FK/check; Documents: optional schema + unique indexes; Graph: relationship endpoint integrity; KV/Blob: key/etag uniqueness) inside the transaction boundary.
- **Isolation** — MVCC snapshots (`ReadCommitted`, `Snapshot` default) with a lock manager for write-write conflicts and `Serializable` upgrade later. Readers never block writers.
- **Durability** — WAL flushed per durability policy before commit acknowledges; group commit batches fsyncs; recovery replays the journal to the last committed LSN on open; torn pages detected via per-page CRC.

Crash/recovery test suites (kill the process mid-commit, replay, verify) are the acceptance bar for R1 — per-model correctness tests build on a shared crash-harness in the kernel.

### 3.7 The platform data layer (embedded consumption)

Other Cohesion resources consume this area in one of two ways:

- **Embedded (the default for platform resources):** reference the model engine packages + `Database.Embedded`, compose engines with `EmbeddedDatabase.Create(...)`, and operate on `IDatabaseEngine`/`IDatabase` directly. Same engines, same ACID, no server process. A resource that hides its data layer from its own consumers pairs the reference with `CohesionPrivateProjectReference` and a `CohesionFrameworkPrivateAssembly` entry (the repo cross-resource pattern — `.claude/rules/build-system.md`).
- **Hosted (shared database service):** depend on a `DatabaseResource` in the application graph (`AddDatabase(...).DependsOn(...)`) and connect through `Database.Client` over the wire protocol. Right when several resources share one database service or the data outlives any single resource.

Because embedded and hosted use the same engine surfaces, a resource can start embedded and move to the hosted service without rewriting its data access. Feature #862 enforces the self-sufficiency invariant and lands a reference adoption (ConfigurationStore or SecretStore) as the pattern-setter.

### 3.8 Transaction/MVCC integration — closing the isolation split-brain (next-iteration design)

**The problem, stated plainly: the area has a split-brain.** `Database.Transactions` ships a complete MVCC manager — `TransactionManager` with snapshot capture and per-level refresh semantics, the S/U/X/IS/IX `LockManager` with wait-for-graph deadlock detection, `VersionStore` with `IVersionStore.PurgeWriterAsync`, the journal-bound `TransactionLog` — and **no engine uses any of it**. The SQL engine isolates through `Database.Storage`'s transactions instead: page-grain, single-writer, no-wait locks (conflict = throw), full-page-image WAL. Consequences today: writers to the same page serialize even on disjoint rows; concurrent readers scan pooled pages with **no snapshot filtering**, so they can observe uncommitted in-flight page state; the `SqlVersionPurgeWorker` is an inert stub because there is no version store to purge. R1's "isolation via MVCC snapshots" is therefore **not yet true end-to-end** — atomicity and durability are real, isolation is page-grain serialization without read versioning. This section is the design for closing that gap; the work items are filed under #862 (the umbrella whose acceptance includes MVCC-backed engines).

The integration architecture, in the order the work items land:

1. **Session ↔ manager binding + isolation levels** (first item). The model engine owns both vocabularies — the root's `IDatabaseTransaction` and the Transactions child root's `ITransactionContext` — so the *engine session* is where they bind (the placement decision recorded with the child-root inversion: the child never sees the root's contracts; whoever owns both translates). `SqlDatabaseSession.BeginTransactionAsync(isolationLevel, ct)` begins an `ITransactionContext` on an engine-level `ITransactionManager` **and** a storage transaction, pairing them for the executor via `IStorageTransactionSource` (`Database.Indexing`'s existing seam for exactly this pairing); `SqlDatabaseTransaction` commits/rolls back through the *manager* (which owns commit ordering and durability await through its journal-bound `ITransactionLog`), with the storage transaction as the physical WAL bracket beneath it. The **isolation-level seam on the root contract is already in place** (this iteration): `IDatabaseSession.BeginTransactionAsync(IsolationLevel, ...)` + `IDatabaseTransaction.IsolationLevel` — the root consumes the child's enum, legal post-inversion; engines carry the level today and honor it conservatively (page-grain is stronger than any level's write behavior), and the binding makes the per-level snapshot semantics real (`ReadCommitted` = per-statement refresh, `Snapshot` = fixed at begin — both already implemented by the manager).
2. **Row-level version stamping + snapshot-visible scans.** Rows in the SQL record space grow writer/deleter `TransactionSequence` stamps — precedent: the B+Tree's leaf entries already carry exactly these MVCC stamps (tombstone deletes; aborted stamps revert physically via page images), so the record layer adopts the proven design. Scans evaluate `TransactionSnapshot.IsVisible(writer)` per row (and treat a visible deleter as absence), which removes the dirty-read window: a reader's snapshot never admits an uncommitted writer. Updates become version-chain writes through the `IVersionStore` where in-place rewrite would destroy a version a live snapshot still needs.
3. **Row-grain write conflicts through the lock manager.** Write-write detection moves from page-grain no-wait to row-grain waits via `ILockManager` — exclusive hashed-key locks, the precedent the B+Tree's uniqueness enforcement already set (insert **and** delete take the key lock). Page-level single-writer locks *remain* beneath as the storage invariant that makes full-page-image logging correct; they stop being the user-visible conflict surface because row locks serialize writers before page contention manifests. Deadlocks surface to sessions as the manager's `TransactionDeadlockException` (retryable by construction), wrapped in a `DatabaseException` at the model boundary per the area error policy.
4. **Version-purge worker activation.** `SqlVersionPurgeWorker`'s body becomes real: drain version chains via `IVersionStore.PurgeWriterAsync` for aborted writers and prune below the manager's `OldestActive` bound — the stub and its inventory slot were kept precisely so this lands without touching the worker seam.

**Migration path (page-grain → row-grain), by construction:** storage transactions are not replaced — they remain the physical WAL bracket (before-images, after-images + commit record, recovery replay), and the MVCC layer sits **above** them; `IStorageTransactionSource` is the seam that pairs an `ITransactionContext` with its `IStorageTransaction`. Each step is independently shippable: binding first (semantics unchanged, level carried for real), stamps + visibility next (dirty reads close), row locks after (concurrency improves), purge last (space amplification bounded). Recovery gains one obligation at step 2: `TransactionRecovery.Analyze` results drive `PurgeWriterAsync` for every sequence the journal cannot prove committed (the contract member exists for exactly this).

### 3.9 Next-iteration root seams (design notes — items exist, contracts deliberately deferred)

- **Health/diagnostics (#168):** the redesigned observational surfaces are the health seam's inputs — `IDatabaseEngine.State` (`Running`/`Faulted`/`Disposed`) + `Workers` (name/kind/cadence), `IDatabaseServerContext.Sessions`, `IDatabaseApplicationContext.Servers`/`Engines`. The intended shape mirrors `Web.Health` (one health package; resources consume it privately; the gateway scrapes `/healthz`): a root-level `I<*>HealthSource`-style aggregation that walks an application context and reports per-engine/per-server condition, surfaced by Hosting through the area's private-Web admin seam. Design only — no root contract until the Web.Health-consumption pattern is proven on a second resource; the observational members above were shaped so the health surface needs no new engine/server members.
- **Backup/restore (#161):** becomes an **engine-level, per-model operation on the data-machine contract** — a data machine that can be snapshotted while running (checkpoint + copy of the file sets; PITR later via WAL archiving per R11), not a host service (embedded consumers need it identically, R10). The storage layer already reserves the backup seams (`.bak` asset naming in the strategy contract). Design note only; the contract lands with #161.
- **Authorization (#177 family):** `Database.Security` (child root) grows the authorization vocabulary (principals, roles, permission checks — beside the existing `IDatabaseAuthenticator` authentication seam), and **per-model enforcement composes in the model packages** (`Sql.Security` enforcing over the SQL catalog's objects), consistent with per-model servers: the server authenticates, the model authorizes. Design note only.

## 4. Gap analysis (2026-07-06)

What existed before this scaffold vs. what a real multi-model OLTP engine needs. Backlog references are the L03.02 tree in GitHub Project #13.

| Gap | Existing coverage | Resolution |
|---|---|---|
| Transactions / MVCC / lock manager — nothing beyond the `IDatabaseTransaction` surface | only task #164 ("transaction boundaries") | New `Database.Transactions` project + new feature under #31 (Core Engine) |
| Shared index infrastructure — `IStorageIndexManager` was a name with no design | indexing mentioned only for Documents (#188) | New `Database.Indexing` project + new feature under #31 |
| Wire protocol + server front-end + client core — `IDatabaseServer` was an empty stub | per-model "client APIs" tasks, no protocol foundation | New `Database.Protocol` + `Database.Server` projects + new feature under #31 (the server runtime was folded into `Database.Hosting` 2026-07-12, then `Database.Server` was resurrected 2026-07-13 as the shared base per-model servers derive from) |
| ApplicationModel manifest — no `Database.ApplicationModel`, no `AddDatabase` | not in backlog | New manifest project + new feature under #31 |
| SDK build tooling — `Migration.targets` and the Tasks project were empty shells | #176 covers migration *workflows* as engine behavior only | SDK scaffold (targets + task skeletons) + new tooling epic |
| Shared type system — `Database.Types` skeletal, no collation/encoding story | #173 covers SQL type coercion only | Fleshed out via new feature under #31; `IndexKey` encoding depends on it |
| `Sql.Replication` missing (matrix asymmetry) | — | Project added |
| Structural defects: `KeyValuePair.Cataalog.csproj` filename typo; `KeyValuePair.Security` test project living under `src/`; stray `using Assimalign.Cohesion.Web` in a root abstraction | — | Fixed in the scaffold PR |
| Graph query standard undecided | #193 (evaluate candidates) | Unchanged — remains the gating decision for Graph language work; ISO GQL recommended |
| Encryption at rest — no coverage anywhere in the L03.02 tree | #807 covers only the Security area's key documents | New feature #861 under #31: page/WAL/backup encryption consuming the `Security.DataProtection` key ring |
| Embedded consumption undefined — `Database.Embedded` was a `Class1.cs` placeholder despite the platform-data-layer role | not in backlog | `EmbeddedDatabase` facade scaffolded (implemented + tested); feature #862 enforces engine self-sufficiency + reference adoption |

Everything else in the existing backlog (storage repair #157–#159, journaling/recovery #160–#162, execution pipeline #163–#165, hosting #166–#168, and the per-model trees #169–#216) mapped cleanly onto this architecture and is sequenced in the program plan.

## 5. AOT posture

Everything under `resources/Database/` builds with `IsAotCompatible=true` (area `Directory.Build.props`). Concretely: document/values serialization is span-based over UTF-8 with source-generated model binding where consumers bring types; protocol frames are hand-encoded value objects; engine composition is static (the host news up engines — no `EngineModel`-keyed activation); the SDK's build tasks run inside MSBuild (not the app) so they are exempt (same sanctioned exception as `analyzers/`).

## 6. Decision log

| Decision | Why |
|---|---|
| Keep the pre-existing project matrix | It matches the backlog 1:1, keeps model semantics out of the kernel, and the owner confirmed the layout intent. Consolidation would churn 50 projects for aesthetic gain. |
| Transactions as its own kernel project (not inside Storage or Execution) | ACID is the platform's defining requirement (R1); the MVCC/lock/snapshot machinery has consumers in every engine and a different change cadence than page I/O. |
| One shared wire protocol, model-agnostic server | Five per-model servers would quintuple the security surface. Model semantics live in message payloads (and per-model clients), not in transport framing. |
| Protocol project split from the server runtime | The client core must speak the protocol without referencing server internals; value-object frames are testable without sockets. |
| `Database.Server` folded into `Database.Hosting` (2026-07-12, owner decision) | The server runtime exists to put engines on the network — the hosting concern (mirrors the Web-area `Web.Server`→`Web.Hosting` direction). Keeping it separate only existed to satisfy COHRES002 and forced a cross-assembly endpoint-adapter seam (`DatabaseServer.CreateHostService`); the fold replaces that with direct composition plus (at the time) the sanctioned per-project `CohesionHostingIsolationExemptions` for `Database.Protocol` + `Database.Security` (the server's own machinery, not hosted features). The exemption was retired by the child-root inversion below. **Half-unwound 2026-07-13** — see "Servers are per-model" below. |
| **Engines are data machines; servers are per-model; the context goes plural** (2026-07-13, owner decision — the approved root redesign) | Three coupled reversals with one root cause: "running" belongs to the per-model server, not the engine. (1) **Engine lifecycle members removed** (reverses the #903 decision that added `StartAsync`/`StopAsync` to the root contract): an engine is operational from creation and disposal is its one transition (quiesce workers → durable flush → close). New information: once servers became per-model, the engine's start/stop was revealed as accidental service-shape — what #903 needed (host-aligned durability) is satisfied by "composition root disposes the engine". A "restart" is a fresh engine over the same storage root. `EngineState` shrank to observational `Running`/`Faulted`/`Disposed` (worker-fault reporting kept a minimal `State` — throwing from dispose is hostile). (2) **The worker claim handshake deleted** (supersedes the #902 claim model): engines spawn their loops at creation on engine-owned dedicated threads (the Lane-H guardrail satisfied engine-side) and quiesce them on dispose; `IDatabaseEngineWorker` is observational (name/kind/cadence). One owner, no two-owner protocol. (3) **`IDatabaseApplicationContext` converges with Web**: plural `Servers` + server-less `Engines`; `IDatabaseApplication` = `Context` + start/stop; the builder's deferred server factory receives the context (`Func<IDatabaseApplicationContext, IDatabaseServer>`), and multiple servers are allowed — one per model. |
| **Servers are per-model; `Database.Server` resurrected as the shared base** (2026-07-13, owner decision — records the partial reversal of the 2026-07-12 fold) | A server fronts exactly one engine (`IDatabaseServerContext.Engine` is singular), so model-specific wire behavior has a home: `SqlDatabaseServer : DatabaseServer`, and each future model ships its own. The new information that changed the fold's calculus: per-model servers must **derive from** the shared machinery, and COHRES001 makes `Database.Hosting` unreferenceable by area libraries — a base class living in the hosting module is underivable by exactly the packages that need it. The generic machinery (session state machine/pump, framing, auth/idle/session guardrails, two-phase drain) therefore moved to the resurrected `Assimalign.Cohesion.Database.Server` (refs: root + `Connections`; Protocol/Security transitively via the root). What the fold got right is retained: `Database.Hosting` still composes servers into the host process — it just no longer owns their implementation, and is now composition-only (root + `Hosting` refs; `Connections` dropped). `DatabaseServerOptions` lost its `Engines` list (the derived server supplies its single engine). |
| **Child roots roll up under the root** (2026-07-13, owner decision) | Unlike the Web area, the database has a vast base-component surface, broken out as child roots for separation of concerns and testability: `Types`, `Language`, `Storage`, `Transactions`, `Execution`, `Protocol`, `Security`, `Governance`. The root references THEM; no child references the root — so each stays independently consumable and one root reference delivers the whole base surface. Consequences: `ProtocolVersion` moved home to `Protocol` (plain static `Current`), `TransactionId`/`TransactionState` moved down to `Transactions`, `ProtocolException`/`TransactionAbortedException` re-rooted on `Exception` (child roots own independent exception roots — `DatabaseException` covers the root and everything above it), and `Database.Hosting`'s COHRES002 exemption became unnecessary (Protocol/Security arrive transitively via the root). The rejected alternative — children referencing the root for shared vocabulary — made Protocol/Transactions unaggregatable and pushed exemptions into the hosting module. |
| No `.Language` for Blob and KeyValuePair | Blob is stream/metadata API-driven; KV commands are simple protocol verbs. A parser would be ceremony. Cache (#208, post-MVP) may still get one. |
| SQL migration tooling in the SDK, apply engine in `Sql.Catalog` | Build-time diffing belongs to MSBuild (R7–R9); runtime apply must be transactional inside the engine. Splitting keeps the SDK task a thin orchestrator. |
| `EngineModel.KeyValueStore` enum name vs `KeyValuePair` project naming | Left as-is for now — renaming the enum member is surface churn with no behavior; revisit before the first public package. |
| Engines are self-sufficient; `Database.Hosting` is composition-only | The area is the platform's data layer (R10): embedded consumers must get identical durability. If the host owned flush/checkpoint work, embedded mode would silently lose ACID. |
| Encryption at rest sits under the buffer pool, keyed by `Security.DataProtection` | Engines/models stay encryption-unaware; key material and rotation policy stay in the one platform key-management stack instead of a second bespoke one (the #774 lesson). |
| `Database.Embedded` elevated from post-MVP to MVP-adjacent | Platform resources (ConfigurationStore, SecretStore, Scheduler) need a data layer before the hosted service matures; embedded-first is how they get it without coupling to server operations. |

## 7. Related documents

- [README.md](README.md) — area overview and project catalog
- [docs/DATABASE_PROGRAM_PLAN.md](../../docs/DATABASE_PROGRAM_PLAN.md) — stages, lanes, blockers, session protocol (temporary)
- `Assimalign.Cohesion.Database.Hosting/docs/DESIGN.md` — host execution-model decisions
- `libraries/ApplicationModel/DESIGN.md` — the two-plane orchestration model `Database.ApplicationModel` plugs into
- `libraries/Hosting/Assimalign.Cohesion.Hosting/docs/DESIGN.md` — the per-service execution menu the host composition follows
- `docs/SERVICE_LAYER_DESIGN.md` §Database, `docs/SERVICE_STORY_REQUIREMENTS.md` §Database — the earlier requirement passes this design reconciles
