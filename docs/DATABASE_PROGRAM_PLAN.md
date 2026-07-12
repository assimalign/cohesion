# Database Program Plan (Data Platform, L03.02)

**Status:** active · **Created:** 2026-07-06 · **Owner:** Chase Crawford · **Scope:** the multi-model OLTP database platform — `resources/Database/*`, the `Assimalign.Cohesion.Sdk.Database` build tooling, and the `App.Database` framework family. GitHub epics **#31 / #4 / #5 / #50 / #57 / #856** (WBS `L03.02.*`).

> **Why this file exists.** This program spans ~77 GitHub work items across 6 epics and will be implemented by many separate AI coding sessions. No single session holds the whole picture in context. This document is the **durable sequencing index**: it records what depends on what, what is safe to do in parallel, and the protocol each session follows. GitHub issues hold the *what* and *acceptance criteria*; this file holds the *when* and *in-what-order*. The architecture itself lives in `resources/Database/DESIGN.md` — read that first; this file assumes it.

This file is temporary scaffolding for the duration of the program. When the five engines reach MVP and this backlog drains, fold anything durable into the relevant `docs/DESIGN.md` files and delete this doc.

---

## 1. How to run this across multiple sessions (read first)

The safe unit of work is **one GitHub issue = one session = one branch = one PR**.

**The session protocol (every session follows this):**

1. **Pick an issue that is unblocked.** An issue is workable only if every entry in its *Blocked by* column (§4) is merged. Never start a blocked issue — its prerequisites define types/seams you would otherwise invent and later fight.
2. **Read four things before coding:** (a) the issue body and acceptance criteria; (b) this plan's row for the issue and the lane guardrails in §3; (c) `resources/Database/DESIGN.md` (the architecture) plus the touched project's `docs/DESIGN.md`; (d) the repo coding rules (`.claude/rules/`, auto-loaded in Claude sessions).
3. **Branch:** `feature/<wbs>-<slug>` naming the issue's WBS (e.g. `feature/L03.02.01.03-mvcc-substrate`). The `cohesion-work-items` skill infers scope-creep placement from this branch.
4. **Implement to the acceptance criteria.** File out-of-scope discoveries with the `cohesion-work-items` skill (don't expand the current issue) and call them out in the PR description so the orchestrator can slot them into §4.
5. **Open a PR** with one `Closes #NNNN` per line (use `New-CohesionWorkItem.ps1 -EmitClosesBlock` from the same worktree).
6. **Do not edit this plan file.** The orchestrator reconciles §5 from merged PRs — that avoids shared-doc merge conflicts when many sessions run in parallel.

**Golden rule for parallelism:** issues in different **lanes** (§3) at the same **stage** (§2) can run concurrently with no coordination. Two sessions in the *same* lane touching the same project must be serialized — check §5 and open PRs for an in-flight sibling before starting.

**Prompt template for a session:**

```
Work GitHub issue #NNNN in assimalign/cohesion.

Before coding, read docs/DATABASE_PROGRAM_PLAN.md — follow the Session Protocol in §1,
confirm the issue is unblocked per §4, and honor the lane guardrails in §3. Read
resources/Database/DESIGN.md for the architecture and follow the repo coding
rules (.claude/rules). Branch, implement to the issue's acceptance criteria, and open a PR that closes it.

If the issue is blocked per §4, stop and tell me which prerequisite is outstanding.
```

---

## 2. Stages (dependency gates)

A stage is a gate, not a calendar. Everything in a stage may proceed once the prior-stage items it depends on are merged. (Stages are finer-grained than the GitHub `Wave` field; treat this document as the authority on order.)

| Stage | Theme | Gate to enter |
|---|---|---|
| **0 — Ground truth** | Area scaffold, design docs, structural fixes. Landed by the scaffold PR (#855). | done |
| **1 — Kernel** | Storage repair + durability, the ACID substrate, type system, execution pipeline, wire protocol. Everything downstream imports from here. | none |
| **2 — Kernel build-out + languages** | B+Tree indexing on the kernel; per-model language work (always parallel-safe); SQL catalog. | its Stage-1 prerequisites |
| **3 — Model engines** | Each model's storage layout, catalog, and engine lifecycle composed from the kernel. | its Stage-1/2 prerequisites |
| **4 — Service surface** | Server/client end-to-end, per-model clients, security, hosting/governance/health, orchestration manifest. | a working engine + protocol |
| **5 — Tooling & hardening** | Database projects, migration engine, conformance corpora, crash suites at scale. Replication is **post-MVP** and intentionally last. | catalog + language stability |

**The three most load-bearing edges in the program:**
- **#158 (page/buffer management) and #160 (journal ordering + recovery)** gate the entire kernel — land them early and review them hardest.
- **#850 (MVCC/transactions)** gates every engine's write path and #851 (indexing).
- **#852 (protocol/server/client core)** gates every per-model client and the hosted end-to-end path.

---

## 3. Lanes (what can run in parallel) + per-lane guardrails

| Lane | Area | Projects | Guardrail (the thing sessions get wrong) |
|---|---|---|---|
| **K — Storage kernel** | pages, WAL, recovery, backup | `Database.Storage` | The journal is the *only* durability mechanism; no side files. Page CRC on every read path. Keep contracts model-agnostic — model layouts live in `{Model}.Storage`, never here. |
| **T — ACID substrate** | transactions, locks, versions, types, indexing | `Database.Transactions`, `Database.Types`, `Database.Indexing` | `TransactionSequence` (ordering) vs `TransactionId` (identity) — don't conflate. Index mutations always take `ITransactionContext`. Type encodings must be order-preserving under raw byte compare. |
| **X — Execution** | sessions, pipeline, plans | `Database.Execution`, root `Database` | Model-agnostic: no SQL/document semantics in shared operators. `QueryRequest`/`QueryResult` families stay abstract; models subclass. |
| **P — Protocol & clients** | wire protocol, server, client core | `Database.Protocol`, `Database.Server`, `Database.Client` | Framing is model-agnostic; model semantics live in payloads. Every limit (payload size, sessions, timeouts) is DoS-critical — enforce at parse/accept. Test over `Connections.InMemory`, never live sockets. |
| **LS / LD / LG — Languages** | per-model parsers | `Sql.Language`, `Documents.Language`, `Graph.Language` | Always parallel-safe (leaf dependencies). Declared-dialect contract before deep implementation; conformance corpora guard the AST. Graph language work gates on the #193 standard decision. |
| **MS / MD / MK / MB / MG — Model engines** | engine + model storage + catalog | `Database.{Sql,Documents,KeyValuePair,Blob,Graph}` + their `.Storage`/`.Catalog` | Compose the kernel — never re-implement paging/WAL/locking locally. Every write path goes through `ITransactionContext`. Engines never depend on other engines. |
| **H — Hosting & ops** | host, governance, health, orchestration | `Database.Hosting`, `Database.Governance`, `Database.ApplicationModel` | WAL flush / page writer = `DedicatedThreadService`; endpoint = `BackgroundService` (Hosting DESIGN.md menu). `*.Hosting` is the ONLY DI/Config/Logging seam. The manifest project never references the runtime. |
| **D — Developer tooling** | SDK build tasks, migrations | `sdks/Assimalign.Cohesion.Sdk.Database` | Build-time work in MSBuild tasks; transactional apply in the engine catalog. Static per-model imports — no runtime plugin loading. |

Cross-cutting rules (all lanes): file-scoped namespaces; `CohesionProjectReference`/`CohesionPackageReference`; **no `Microsoft.Extensions.*`**; `IsAotCompatible=true`, no reflection; interface-first with internal impls; XML docs on public APIs; Shouldly tests co-located; update the touched project's `docs/DESIGN.md` in the same change. The rules in `.claude/rules/` are canonical.

---

## 4. The work items (with blockers)

"Blocked by" lists *hard* prerequisites (types/seams that must exist first). Soft coordination is noted inline. Feature-level issues (4-segment WBS) appear where their task children (5-segment) are the real work units — sessions work the tasks; features close when their children do.

### Stage 1 — Kernel

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #157 | K | Fix missing storage model types + align storage abstractions | — (scaffold PR restored `StorageModel`; the *alignment* remains) |
| #158 | K | Page allocation, pinning, eviction, buffer reuse | #157 · **(gates the kernel)** |
| #159 | K | Tests: corruption detection, concurrency, lifecycle edges | #158 |
| #160 | K | Journal write ordering + recovery replay rules | #157 · **(gates durability)** |
| #161 | K | Backup/restore flows + version compatibility | #160 |
| #162 | K | Crash/restart/restore suites proving durability | #160 |
| **#850** | **T** | **Transactions, MVCC, concurrency control (ACID substrate)** | #160 (durable commit path; contract + in-memory work can start immediately) · **(gates all engine write paths)** |
| #854 | T | Shared type system and collation foundation | — |
| #163 | X | Common execution context and pipeline contracts | — |
| #164 | X | Transaction boundaries, commit/rollback semantics, error propagation | #850, #163 |
| #165 | X | Tests: cancellation, failures, multi-stage execution | #163 |
| **#852** | **P** | **Wire protocol, server front-end, shared client core** | #163 (execution dispatch; framing/handshake work can start immediately) · **(gates all clients)** |

### Stage 2 — Kernel build-out + languages (all language items are parallel-safe from day one)

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| **#851** | **T** | **Shared index infrastructure: B+Tree, keys, cursors** | #158, #160, #850, #854 |
| #169–#171 | LS | SQL parser coverage, AST/diagnostics stability, conformance corpora (feature #39) | — |
| #172–#174 | LS | Declared SQL dialect contract, type/builtin coverage, coercion tests (feature #40) | #854 (type identities) |
| #181–#183 | LD | OQL grammar/AST contract, diagnostics, conformance corpora (feature #45) | — |
| #193 | LG | **Decision:** select the graph query standard (ISO GQL recommended) | — · **(gates all Graph language/engine work)** |
| #194–#195 | LG | Graph grammar/AST/diagnostics + conformance plan (feature #52) | #193 |
| #175 | MS | Relational catalog objects + metadata persistence | #158, #164 |
| #177 | MS | Relational security: principals, permissions, schema changes | #175 (shared contracts in `Database.Security` co-evolve) |

### Stage 3 — Model engines

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #178 | MS | Logical/physical planning from SQL AST + catalog | #170, #175, #851 |
| #179 | MS | Execute relational plans against shared storage | #178, #164 |
| #184–#186 | MD | Document query planning, projection/aggregation/mutation, tests (feature #46) | #181, #163 |
| #187 | MD | Document persistence + versioned metadata | #158, #160, #850 |
| #188 | MD | Document index definitions + maintenance | #851, #187 |
| #189 | MD | Document serialization rules (objects/arrays/scalars) | #854 |
| #205 | MK | KV storage + metadata handling | #851, #850 |
| #208–#210 | MK | Cache command surface, eviction/expiry, coherence tests (feature #60) | **post-MVP** (behind #205) |
| #211 | MB | Blob persistence, chunking, metadata catalog, lifecycle | #158, #160, #850 |
| #196–#198 | MG | Graph schema/traversal contracts, diagnostics, tests (feature #53) | #193 |
| #199 | MG | Durable graph storage + adjacency layout | #158, #850, #851 |
| #200 | MG | Graph catalog: schema and object discovery | #199 |

### Stage 4 — Service surface, hosting, orchestration

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #166 | H | Hosting adapters + lifecycle alignment for database workloads | #163; engines start composing as they land |
| #167 | H | Quotas, tenancy/isolation boundaries, audit events | #166 |
| #168 | H | Health, readiness, diagnostics surfaces | #166 |
| **#853** | **H** | **ApplicationModel manifest + AddDatabase integration test** | #166 |
| #180 | MS | SQL client: commands, result sets, errors, telemetry | #852, #179 |
| #190 | MD | Document client APIs: queries, mutations, diagnostics | #852, #184 |
| #207 | MK | KV client APIs, result models, diagnostics | #852, #205 |
| #214 | MB | Blob client: upload/download/metadata (streaming) | #852, #211 |
| #202 | MG | Graph client APIs and error surfaces | #852, #199 |
| #176 | MS | Migration workflows, compatibility checks, rollback (catalog side) | #175 |
| #191, #201, #206, #212 | M* | Per-model security rules and protected-operation checks | #177 (shared pattern), each model's catalog |
| **#862** | **H** | **Embedded consumption: engine self-sufficiency + reference resource adoption** | #850; at least one engine with working lifecycle (#187 or #205 recommended first) |
| #213 | MB | Tests: large-object persistence, metadata, access control | #211, #212 |

### Stage 5 — Tooling & post-MVP

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| **#859** | **D** | **Database project system: declarative schema compile** | #170 (stable AST), #172 (declared dialect) |
| **#857** | **D** | **Migration engine: diff, script gen, transactional apply** | #859, #176 |
| **#858** | **D** | **Model-specific build-tool loading + per-model targets** | #859 |
| **#861** | **K** | **Encryption at rest (pages/WAL/backups) via the Security.DataProtection key ring** | #158, #160, #162 (crash suites must exist to re-run under encryption) |
| #192, #203–#204, #215–#216 | M* | Per-model replication topology/consistency/tests | **post-MVP** — sequenced after every engine reaches MVP; shared log-shipping seam over the WAL comes first (new item to file when scheduled) |

### MVP definition (what "done" means for the first cut)

Every model: create/open/drop databases; CRUD in its native shape; ACID (crash-recovery suite passes); reachable through the wire protocol with a working client. SQL additionally: declared dialect subset with conformance corpus, catalog + migrations. Explicitly **not** MVP: Cache model, replication, cost-based planning, Serializable isolation, cross-model queries.

---

## 5. Progress Log (orchestrator-reconciled from merged PRs)

Sessions do not edit this table; the orchestrator reconciles it from merged PRs.

| Date | Issue | PR | Notes |
|---|---|---|---|
| 2026-07-11 | #851 | (fable5 batch PR) | Stage 2, Lane T. B+Tree over shared pages: one node/page sorted-directory layout on `PageType.Index` (`MaxKeyLength` 1 KiB), recursive leaf/internal splits w/ root growth, sibling chain for range scans; MVCC leaf entries (writer+deleter stamps; tombstone deletes; aborted stamps revert physically via page images); ALL page mutations via NEW public `IStorage.OpenPageForWrite`/`AllocatePageForWrite` (before-image coverage ⇒ crash-mid-split reverts, committed splits replay — both crash-tested); **uniqueness = latest-state check under an exclusive hashed-key lock (inserts AND deletes)** — snapshot-visibility checks allow write skew, caught by test; tree-level RW latch, cursors materialize under read latch; `IStorageTransactionSource` pairs contexts w/ storage txns; directory persistence deliberately catalog-owned (`IIndexRegistry`/`BTreeIndexRegistration` export + `ExistingIndexes` re-attach); `IndexKey.From(DatabaseKeyWriter)` bridges #854 composite keys. 15 tests: point/range/reverse+inclusivity, 2000-entry split ordering, 1000-random-key property scan, composite significance property, MVCC invisibility/snapshot pinning, tombstone snapshots, unique reject/re-insert, concurrent unique writers serialize via lock manager, crash-mid-split revert, committed-splits-survive-restart (no page flush), manager lifecycle. |
| 2026-07-11 | #163, #164, #165 | (fable5 batch PR) | Stage 1, Lane X. Execution substrate: `QueryExecutionContext` (request + scope + RequestAborted + thread-safe diagnostics + item bag), middleware-shaped `IQueryPipeline`/`IQueryPipelineStage`/`QueryPipelineBuilder` (stages wrap/short-circuit; operator trees stay in model planners per lane guardrail), `IQueryTransactionScope` boundary seam (Execution can't reference Transactions — root→Execution + Transactions→root would cycle; engines adapt), boundary rules enforced at the pipeline edge: implicit+Success→commit, implicit+failed-result→rollback-but-return, exception/cancellation→rollback-then-propagate (rollback failure faults scope, never masks root cause; rollback uses CT.None so aborted requests still release), explicit scopes untouched. Concrete `QueryStatementResult`; `QueryExecutionException` local root; tests csproj was an empty shell — now real. 10 tests: ordering, short-circuit, commit/rollback boundaries, error propagation, rollback-failure masking, explicit-scope hands-off, pre/mid-execution cancellation, diagnostics accumulation. |
| 2026-07-11 | #850 | (fable5 batch PR) | Stage 1, Lane T — the ACID substrate. Implementations for all five contracts: `TransactionManager.Create` (sequence assignment, live active table, per-access ReadCommitted snapshot refresh vs fixed Snapshot; commit awaits durability while still in the active table then releases locks as a set; durability failure ⇒ purge+`TransactionAbortedException`+Faulted; `OldestActive` prune bound), `LockManager.Create` (S/U/X/IS/IX matrix, same-owner upgrades, FIFO wake, wait-for-graph deadlock detection, requester-closes-cycle victim policy, cancellable waits), `VersionStore.CreateInMemory` (newest-first chain resolution, prune-below-oldest-active, `PurgeWriterAsync` — NEW contract member: snapshot has no commit-log awareness so the store must unlink aborted writers), `TransactionLog.CreateInMemory`/`CreateJournalBound(IJournal)` (commit = append + `EnsureDurable`; group commit via shared fsync), `TransactionRecovery.Analyze` (committed iff durable commit record). 22 new tests (27 total): snapshot-vs-readcommitted visibility, own-writes, rollback purge, lock release on commit, durability-failure abort, compat matrix/upgrade/FIFO/deadlock-victim/cancellation, prune/purge, journal-bound crash-mid-commit atomicity. |
| 2026-07-11 | #854 | (fable5 batch PR) | Stage 1, Lane T. Database.Types fleshed out: explicit `Collation` identity (binary = code-point order matching UTF-8 bytes — not CompareOrdinal; invariant = culture-invariant linguistic; ids persisted in keys, sealed set = persistence contract), order-preserving self-describing key codec `DatabaseKeyWriter`/`DatabaseKeyReader` (tag byte per component: nulls first, cross-type by tag; sign-flipped BE integers; IEEE-754 total-order fold w/ positive-NaN canonicalization — .NET double.NaN is negative-signed, caught by test; decimal = normalized scientific digits w/ complemented negatives; linguistic strings = sortkey + round-trip original; zero-escaped var-length payloads; DateTime kind preserved non-ordering; Guid RFC-4122 BE), `DatabaseTypeException` root. Empty placeholder classes (block-scoped ns) deleted; csproj cleaned; NEW tests project wired into all 3 slnx files; docs OVERVIEW/DESIGN. 16 ordering/round-trip tests incl. full-range integers, ±∞/−0.0/NaN, decimal scale mix, embedded-zero binaries, composite significance, prefix pairs. |
| 2026-07-11 | #160, #162 | (fable5 batch PR) | Stage 1, Lane K — the durability keystone. WAL v2: typed binary records (begin/commit/rollback/checkpoint/before-image/after-image/logical-op; long txn sequence, string-heavy logical journal removed), `IJournal`+`StreamJournal` with CRC frames + torn-tail tolerance. Storage transactions (`IStorageTransaction`): before-image at first touch, buffer-pool write-ahead gate (steal-safe), commit = after-images + commit record + fsync (no-force), rollback restores pages in memory, page-level single-writer locks. Recovery = last-record-wins over committed after-images ∪ uncommitted before-images, exact-LSN idempotency, runs on open, ends in checkpoint; `Checkpoint()` = durable flush + journal truncation with continued LSNs; clean shutdown checkpoints. SQL engine rewired onto storage transactions (parameter-bridge executor keeps behavior; **rollback now actually undoes in-memory changes** — prior defect). 10-test crash suite (`CrashSimulationStream` harness: flush-gated journal, write-through data): redo-without-page-flush, crash-before-commit with stolen writes, stolen-update undo, repeated-replay idempotency, rollback+crash, clean-shutdown checkpoint, interleaved txns, page write-conflict, completed-txn guards, checkpoint-vs-active-txn; + 9 journal codec/ordering tests; + 2 SQL-level durability tests (rollback visibility, engine-restart recovery). 55 Storage / 27 Sql / 14 SqlStorage green. |
| 2026-07-11 | #158, #159 | (fable5 batch PR) | Stage 1, Lane K. Buffer pool build-out: true LRU eviction over unpinned entries (pins/hits touch to MRU; pinned pages skipped; all-pinned overflow fails loudly), buffer reuse via a recycle stack of pinned 8 KiB buffers (steady-state churn allocates nothing; fresh pages zeroed on reuse), failed/corrupt loads never poison the cache, page-manager allocation recycles freed pages and guards unallocated reads. 12-test suite: LRU order, pinned-eviction refusal, dirty write-back w/ checksum, stale-buffer leakage, corrupted-load cache hygiene, 8-worker pin/unpin churn, concurrent writer persistence, alloc/free/realloc lifecycle. |
| 2026-07-11 | #157 | (fable5 batch PR) | Stage 1, Lane K. Storage model aligned: page header redone (proper 4-byte CRC-32 + 8-byte page LSN; removed the checksum/PageId union hack), `PageType` fixed (`Free = 0` discriminator, `[Flags]` removed), `PageFlags` normalized, CRC verified on every buffer-pool load / stamped on every write-back (`StorageCorruptionException`), file header moved into page 0's body so page 0 is checksummed like every page (+`LastCheckpointLsn` field), free-space map rebuilt from page headers on open (freed pages recycle across reopen; O(1) `IsAllocated`), oversized-record guard, dead `IStorageIndexManager`/`IStorageCatalogManager` stubs removed (Indexing DESIGN.md updated), `JournalException` joined the `StorageException` root; Storage docs (OVERVIEW/DESIGN) created; 10 new alignment tests. |
| 2026-07-06 | #855 | (this PR) | Stage 0. Area scaffold: `resources/Database/DESIGN.md` (requirements + gap analysis + decision log) and README; new projects `Database.Transactions` (MVCC snapshot implemented+tested), `Database.Indexing` (IndexKey encoding implemented+tested), `Database.Protocol` (frame codec implemented+tested), `Database.Server`, `Database.ApplicationModel` (AddDatabase working), `Database.Sql.Replication`; model-root contracts + engine shells for Documents/Graph/KV/Blob; `Sdk.Database` migration targets + task skeletons; fixes: `StorageModel` restored (part of #157 — alignment remains open), `SqlDatabaseEngine.Model`, `Cataalog.csproj` typo, misplaced KVP.Security tests, IDE0011 breaks in Storage + Sql.Language. Filed #850–#854 (kernel features), #856 (tooling epic), #857–#859. Second pass (same PR): `EmbeddedDatabase` facade implemented + tested (platform data-layer requirement R10), enterprise requirements captured as R11, filed #861 (encryption at rest) and #862 (embedded consumption + engine self-sufficiency). |

---

## 6. Fast reference

- Architecture: `resources/Database/DESIGN.md` · Area catalog: `resources/Database/README.md`
- Epics: Core Engine **#31** · SQL **#4** · DocumentDB **#5** · GraphDB **#50** · KV/Blob/Cache **#57** · Tooling **#856**
- Fan-out prerequisites to land first: **#158, #160 → #850 → #851** (kernel spine) and **#852** (client spine)
- Gating decision: **#193** (graph query standard) — until decided, only #193 itself is workable in the Graph lanes
- Hosting model: `libraries/Hosting/.../docs/DESIGN.md` (execution menu) · Orchestration: `libraries/ApplicationModel/DESIGN.md`
- Platform data layer: other resources consume engines **embedded-first** via `Database.Embedded` (DESIGN.md §3.7); engines must stay self-sufficient (#862)
- Work-item mechanics: `.claude/skills/cohesion-work-items/` (`-EmitClosesBlock` for PR close-out)
