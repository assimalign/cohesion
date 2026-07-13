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
| R4 | **Hosting via `libraries/Hosting`.** The database server is a `Host<DatabaseApplicationContext>` whose internal services follow the per-service execution menu: WAL flush and page writer on `DedicatedThreadService` (synchronous blocking I/O, dedicated threads), protocol endpoint on `BackgroundService` (async accept loop). No second lifecycle model. | `Database.Hosting` |
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
│                    DI/Config/Logging seam — the ONLY DI seam — and  │
│                    the server runtime: accept loop, sessions, drain)│
├─────────────────────────────────────────────────────────────────────┤
│ Service surface    Database.Protocol ── Database.Client             │
│                    Database.Security · Database.Replication · Database.Governance
├─────────────────────────────────────────────────────────────────────┤
│ Model engines      Sql · Documents · Graph · Blob · KeyValuePair    │
│  (per model)       {Model} (engine) · {Model}.Language · {Model}.Storage
│                    {Model}.Catalog · {Model}.Client · {Model}.Security
│                    {Model}.Replication                              │
├─────────────────────────────────────────────────────────────────────┤
│ Kernel (shared)    Database (contracts) · Database.Execution        │
│                    Database.Transactions · Database.Indexing        │
│                    Database.Storage (pages, buffer pool, WAL,       │
│                    recovery) · Database.Types · Database.Language   │
└─────────────────────────────────────────────────────────────────────┘
```

Dependency direction is strictly downward. Model engines depend on kernel projects; kernel projects never depend on a model. The service surface depends on the root contracts (`Database`, `Database.Execution`) and is model-agnostic: the server dispatches to whichever engines the host composed.

### 3.2 The kernel

- **`Database`** — the public contract root: `IDatabase`, `IDatabaseEngine`, `IDatabaseSession`, `IDatabaseTransaction`, `DatabaseException` (the area exception root), `DatabaseName`, `TransactionId`, lifecycle enums. Model-agnostic; every other project references it.
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
- **The server runtime** — the network front-end: accepts connections (via `libraries/Connections` drivers), authenticates a session principal, binds a session to a database, and pumps protocol frames into `IDatabaseSession` executions. Model-agnostic — it serves whatever engines the host registered. Originally the separate `Database.Server` project; folded into `Database.Hosting` on 2026-07-12 (the server exists to put engines on the network, which is the hosting concern — see §3.5 and the decision log).
- **`Database.Client`** — the shared client core: connection strings, connection pooling, protocol client, result materialization. Per-model `.Client` projects add typed surfaces on top.
- **`Database.Security`** — authn/authz contracts (principals, roles, permission checks) consumed by the server and per-model security projects.
- **`Database.Replication` / `Database.Governance`** — shared replication contracts (log shipping seam over the WAL) and operational governance (quotas, tenancy, audit events). Post-MVP build-out; contracts stay in place so model services don't invent local equivalents.

### 3.5 Hosting and orchestration

`Database.Hosting` owns the server runtime (`IDatabaseServer`, folded in from `Database.Server` on 2026-07-12 — the fold takes the sanctioned per-project COHRES002 exemption for `Database.Protocol` + `Database.Security`, the server's own machinery) and composes the host: engine(s) + `DatabaseServer` + the internal services (`WriteAheadFlushService`, `PageWriterService` as `DedicatedThreadService`; the protocol endpoint as `BackgroundService`). It is the only project that touches DI/Configuration/Logging (repo rule: `*.Hosting` is the only DI seam). Per the engine self-sufficiency principle (R10), the host's services *map* engine-owned workers onto the execution menu — they do not own durability work themselves, or embedded consumers would lose it. `Database.ApplicationModel` is manifest-only: `DatabaseResource` declares artifact `Assimalign.Cohesion.Database.Application`, one declared endpoint (`db` / scheme `cohesion-db`, platform-allocated port by default), and a persistent data volume mount; `AddDatabase(...)` composes it into an application graph. When the repo-wide `.Hosting` → `.Application` rename lands (ApplicationModel DESIGN.md Phase 3), Database follows.

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

## 4. Gap analysis (2026-07-06)

What existed before this scaffold vs. what a real multi-model OLTP engine needs. Backlog references are the L03.02 tree in GitHub Project #13.

| Gap | Existing coverage | Resolution |
|---|---|---|
| Transactions / MVCC / lock manager — nothing beyond the `IDatabaseTransaction` surface | only task #164 ("transaction boundaries") | New `Database.Transactions` project + new feature under #31 (Core Engine) |
| Shared index infrastructure — `IStorageIndexManager` was a name with no design | indexing mentioned only for Documents (#188) | New `Database.Indexing` project + new feature under #31 |
| Wire protocol + server front-end + client core — `IDatabaseServer` was an empty stub | per-model "client APIs" tasks, no protocol foundation | New `Database.Protocol` + `Database.Server` projects + new feature under #31 (the server runtime later folded into `Database.Hosting`, 2026-07-12) |
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
| `Database.Server` folded into `Database.Hosting` (2026-07-12, owner decision) | The server runtime exists to put engines on the network — the hosting concern (mirrors the Web-area `Web.Server`→`Web.Hosting` direction). Keeping it separate only existed to satisfy COHRES002 and forced a cross-assembly endpoint-adapter seam (`DatabaseServer.CreateHostService`); the fold replaces that with direct composition plus the sanctioned per-project `CohesionHostingIsolationExemptions` for `Database.Protocol` + `Database.Security` (the server's own machinery, not hosted features). |
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
