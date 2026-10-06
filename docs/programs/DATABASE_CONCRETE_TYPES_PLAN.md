# Database concrete-first types: plan of record

**Status:** Phase 0 landed with this file; phases 1 (#1257) and 2 (#1258, kernel and wire tracks)
re-verified and implemented on 2026-10-05 (§7, §6.9); phase 3 (#1259) re-verified, implemented
and reviewed on 2026-10-05, with two owner questions open at its merge (§7, §6.4, §6.5); step
P4.0 (#1260) re-verified, implemented and reviewed on 2026-10-05 (§7, §6.5); the KeyValuePair model PR of
P4 (#1260, the first of five) re-verified and implemented on 2026-10-05 (§7, §6.4, §6.5, §6.9); the
Graph model PR of P4 (#1260, the second) re-verified and implemented on 2026-10-05 and reviewed on
2026-10-06 (§7, §6.4, §6.5, §6.9); the Documents model PR of P4 (#1260, the third) re-verified,
implemented and reviewed on 2026-10-06 (§7, §6.4, §6.5, §6.6, §6.9); the Blob model PR of P4
(#1260, the fourth) re-verified, implemented and reviewed on 2026-10-06, with three interim
choices pending owner confirmation (§7, §6.4, §6.5, §6.6, §6.9); the Sql model PR
of P4 (#1260, the fifth and last) re-verified and implemented on 2026-10-06, with the
closed-database fix and the worker's release hook (§7, §6.4, §6.5, §6.7, §6.9) ·
**Created:** 2026-10-04 · **Owner:** Chase Crawford
**Epic:** #1255 (`L03.02.01.56`) · **Phases:** #1256 to #1264 · **Rule:** `.claude/rules/database-area.md`
· **Owner decision:** O34a in `docs/DEVELOPER_EXPERIENCE_DESIGN.md` · **Supersedes:** #1232
**Branch:** every phase branches from the integration branch
`claude/database-inventory-sql-expansion-aa27d1` (draft PR #1168) and merges back.

> **Why this file exists.** It is the sequencing record for one program: moving the Database area
> off its 106 public interfaces without a red build at any step. Each phase's GitHub issue holds the
> acceptance criteria. This file holds the inventory, the order, and the critique corrections every
> phase has to honor. The binding rule text is `database-area.md`. Each phase moves its own
> `docs/DESIGN.md` text with its code (`documentation.md`). When phase 8 lands, the durable parts of
> this file move into those DESIGN files and this file is deleted.

Confidence tags follow the design review that produced this plan. **[Certain]** means checked in
this tree on 2026-10-04 or in a cited reference source. **[Likely]** means a strong inference.
**[Guessing]** means a gap filled by judgment. File references are `path:line` at commit `3f379cca`.

### Owner decisions of 2026-10-06

The owner accepted every open recommendation on 2026-10-06. They settle the questions this file
records as open or "pending owner confirmation" elsewhere; where an older paragraph says
otherwise, this list wins.

| # | Question | Decision |
|---|---|---|
| 27 | `StorageJournal` and `BTreeRecordVersionIndex` public constructors (§5.3, row 40) | `Create(...)` factories, no public constructors (rule 1). |
| 28 | `DatabaseAuthenticator.AuthenticateAsync` null and cancellation checks (row 27) | Accepted (rule 4). |
| 29 | `ProtocolFrameWriter` payload bound in the stream leaf (row 25) | Moves into the base's public `WriteFrameAsync` (rule 4). |
| 30 | Server session version and principal set after the handshake (row 11) | Protected one-shot setters stay; rule 6 is amended to say so (O34a). |
| 31 | A server start refused because the engine is not Running (row 9) | Terminal, as `DatabaseServer` makes it. |
| 32 | `DocumentDatabase`/`BlobDatabase` session-less collection and container operations (§6.6) | Removed: every model's operations take the session. |
| 33 | Reopening a database closed outside its engine (#1289) | The engine forgets it, so a later open reopens it from disk. |
| 34 | SQL's coded aborted-transaction error (§6.4) | `COHSQLT005`. |

Decisions 22-26 of the same day concern the storage and hosting runtime, not this program's
types: hosted servers reopen an offline database with backoff (health Unhealthy until it
reopens); a deferred checkpoint runs at the end of the statement holding the gate; every
unconfirmed-commit message leads with the model's code (#1272); persistent worker failures take
the database offline after N consecutive failures or a journal-size cap; and the
`Storage.CommitDurability` setter is validated.

---

## 1. What this program buys, and what it does not

- **It does not make the engine faster.** [Likely] Under NativeAOT, a call site that sees three or
  fewer types devirtualizes. Past that, an abstract call is about 1.5 ns cheaper than an interface
  call, and the run-to-run variance is about ±60% (§9). Model-level calls happen once per
  operation, so moving them from interfaces to abstract classes does not change throughput.
- **The bottleneck that matters is elsewhere.** [Certain] Storage journals two full 8 KiB images
  per touched page per transaction: a `BeforePageImage` on the first touch
  (`Database.Storage/src/Storage.cs:1283-1284`) and an `AfterPageImage` at commit (`:983-984`).
  That is #1236, a journal record-design problem. This program does not touch it, and #1236 lands
  before phase 2 because both rewrite `Storage.cs`.
- **What it does buy:**
  - [Certain] No casts. `resources/Database` has 270 casts today: 258 to a model database
    interface (`(ISqlDatabase)` and its siblings) and 12 to a model engine
    (`(SqlDatabaseEngine)` and its siblings). Studio, the templates and cohesion-examples have more.
  - One copy of each shared contract. The explicit-transaction state machine exists four times
    and the "transaction already active" check five times, with three different messages (§6.4).
  - Sealed hot types: `StorageTransaction`, `StoragePageHandle`, `BTreeIndex`, `TransactionContext`.
  - [Likely] 53 to 58 fewer public types (§10).
- **It is a source break.** [Certain] Tag `v10.0.0-preview.1` (`c0dbbd42`) contains all 106
  `Abstractions/I*.cs` files. Owner decision 2026-10-04 (#1152): no `[Obsolete]` shims, no
  compatibility layer, no upgrade path. The release notes carry one line.

## 2. Decisions this plan records

The owner approved every recommendation on 2026-10-04, after giving the direction on 2026-10-03
(#1255). As they bind implementation:

| # | Decision | Where it lands |
|---|---|---|
| D1 | The Database area deviates from "Public APIs use interfaces" and "Interface-first with a guided abstract base". The deviation is scoped to the area by `.claude/rules/database-area.md`, with a code marker and a change-summary line (`deviations.md`). | P0 |
| D2 | In scope: the five models, the kernel child roots (Storage, Transactions, Indexing, Protocol, Security) and the clients. | P1 to P5 |
| D3 | Exactly five interfaces stay: `IDatabaseApplication`, `IDatabaseApplicationBuilder`, `IDatabaseApplicationContext`, `IDatabaseResourceDescriptor` and `IDatabaseApplicationTestFactory`. | P6 retypes the first three |
| D4 | `IDatabase` becomes `DatabaseInstance`, never `Database`. [Certain, probe] Code in a namespace such as `Acme.Database` that names the type fails with CS0118. | P3 |
| D5 | Builders become sealed with internal constructors. This supersedes the 2026-10-02 ruling ("meant to be implemented elsewhere", `Database.Sql/docs/DESIGN.md:627-631`), its 2026-10-03 narrowing, and #1232. | P4 |
| D6 | Deleted: the unused Execution query pipeline, `Database.Governance`, and the Replication placeholders. | P1 |
| D7 | The existing per-row result types (`QueryRow`, `QueryResultSet`, …) are tightened too. | P8 |
| D8 | Blob and Documents sessions use option B: the typed session exposes the operations, and `session.Database` returns the unbound database. | P4 |
| D9 | All five storage strategies become `internal abstract` (critique correction, §4). | P4 |
| D10 | #1236 lands before phase 2. | Gate on P2 |
| D11 | Breaking the preview.1 surface is accepted, with a release-notes line. | Release |
| D12 | Diagnostics and other result collections are never null. `QueryResult.Diagnostics` returns an empty list instead of `null`, which matches #1228's `GraphSchemaResult<T>.Diagnostics` (§6.8). A member where `null` means "absent" keeps it. | P8 |

**Not decided:** whether an analyzer (`COHDB0xx` under `analyzers/`) should reject deriving from
the root bases outside `resources/Database`. The review made no recommendation, so the approval
did not cover it. The program proceeds without one, and phase 8 raises it again if a phase review
finds misuse (§11).

## 3. Principles

The binding text is `database-area.md`. This section records the evidence behind it.

- **Sealed by default. Abstract only for a variant set, for Hosting, or for an inverted seam**
  (§8 records which case each base meets). PostgreSQL keeps
  its variant sets behind routine tables (`TableAmRoutine`, `src/include/access/tableam.h:322`;
  `IndexAmRoutine`, `src/include/access/amapi.h:233`), and its core managers stay single concrete
  implementations. RavenDB's `DocumentDatabase` is a concrete class
  (`src/Raven.Server/Documents/DocumentDatabase.cs:87`).
- **NVI, the ADO.NET shape.** A public non-virtual member calls a protected abstract core
  (`DbConnection.BeginTransaction`, `System.Data.Common/.../DbConnection.cs:56`, over
  `BeginDbTransaction`, `:51`). A capability is a flag plus a throwing virtual
  (`DbTransaction.SupportsSavepoints`, `DbTransaction.cs:83`). ADO.NET makes the flag itself
  virtual; here it is a constructor-set field (rule 6), so reading it makes no virtual call. A
  virtual member that falls back to another member is banned: the default
  `DbConnection.OpenAsync` runs the synchronous `Open` (`DbConnection.cs:324-341`).
- **Constructor visibility follows assembly topology.** The root bases' leaves live in model
  assemblies, and shipped-to-shipped `InternalsVisibleTo` is banned, so those constructors are
  `protected`. Npgsql closes `NpgsqlDataSource` with an internal constructor (`NpgsqlDataSource.cs:96`),
  but that only works inside one assembly. [Certain, probe] A base with a `private protected`
  constructor can be derived from by its friend test assembly, and any other assembly fails with
  CS0122.
- **Typed surface without casts.** The runtime and NativeAOT support covariant returns of classes
  (`RuntimeFeature.CovariantReturnsOfClasses`, `RuntimeFeature.cs:30`). Async factories use
  Npgsql's `new` shape (`public new ValueTask<NpgsqlTransaction> BeginTransactionAsync`,
  `NpgsqlConnection.cs:627`) and await the base NVI member. Construction-fixed references are base
  fields, and the leaf re-exposes them typed with `new` (critique correction, §6.5).
- **No generic virtual methods.** ILC never devirtualizes them
  (`ILCompiler.Compiler/Compiler/ILScanner.cs:728`).
- **The devirtualization limit.** NativeAOT guards at most 3 types when optimizing for speed and 1
  when optimizing for size (`src/coreclr/jit/compiler.h:8272-8292`), with a hard maximum of 5
  (`jitconfigvalues.h:744`). A host running all five models makes every root call site megamorphic.
  That is why abstract root bases are slightly better than interfaces there, and why it does not
  matter at one call per operation.
- **Tests fake through the bases, not through interfaces.** Model behavior tests use real
  in-memory engines, as RavenDB does (`RunInMemory`, `Config/Categories/CoreConfiguration.cs:60-61`).
  Npgsql's `NpgsqlConnection` is sealed (`NpgsqlConnection.cs:28`).

Npgsql references are to a snapshot of `npgsql/npgsql` `main` taken for the design review. No
local clone exists. The .NET references are to the local `dotnet/runtime` clone, and the
PostgreSQL and RavenDB references to the clones under `C:\Source\repos`.

## 4. Critique corrections folded in

The design review's adversarial critique returned `needs-changes`: 14 problems and 10 missing
items. Each item is resolved below. Line references were re-measured on 2026-10-04, after #1226,
#1228 and #1237 landed. Several had moved.

| # | Critique item | Resolution | Phase |
|---|---|---|---|
| C1 | **Blocker.** P1 cannot make `ITransactionManager` internal: `TransactionCoordinator.Manager` exposes it (`TransactionCoordinator.cs:106`), Indexing.Tests builds one (`IndexTestHarness.cs:26-27`, `:39-40`, `:52`), and Sql.Tests reads it (`SqlMvccBindingTests.cs:63`, `:71`). | `TransactionManager` stays public as a sealed type with an internal constructor and a public static `Create`. The Transactions → Transactions.Tests grant (already present for the deferred-undo clock) is the only one P1 relies on; P1 adds none. Foreign tests are rewritten (§6.9). The gate includes Indexing.Tests and Sql.Tests. | P1 |
| C2 | Four public static factories collide with the planned type names (`LockManager`, `TransactionManager`, `TransactionLog`, `VersionStore`). Three more take or return deleted interfaces: `SqlSchemaCompiler`, `ProtocolFraming`, `TransactionRecovery`. | Each gets a name decision in §5.2. | P1, P2, P4 |
| C3 | The statement-snapshot view cannot be internal: Documents, Graph and Blob build it from other assemblies. | `public TransactionContext PinStatementSnapshot()` (§6.1). | P2 |
| C4 | The four `IStorageTransactionSource` wrappers throw different exception types. Indexing may not reference `DatabaseException`. | A per-engine delegate on `BTreeIndexManagerOptions`. Each engine keeps its own exception vocabulary (§6.3). | P2 |
| C5 | The root bases need protected attach semantics. A covariant `Engine` contradicts field-backed getters. `DatabaseEngineWorker` breaks NVI. `VersionStore`'s leaves share one assembly. | Non-virtual `protected` attach, frozen after build. Typed fields behind `new` properties. `DatabaseEngineWorker` through NVI. A `private protected` `VersionStore` constructor (§6.5, rows 5, 7 and 45). **At P3:** the attach, the freeze, the base fields and the worker's NVI landed as resolved (§6.5, rows 5 and 7). | P2, P3 |
| C6 | Option A gives `Dispose` two meanings on one sealed Blob or Documents type. | Option B (§6.6). | P4 |
| C7 | The Sql.Schema declaration records are positional and cannot be closed. | The declaration model stays internal behind an opaque `public sealed SqlSchema`, and `SqlSchemaCompiler` becomes internal (§6.7). Correction to the critique: Sql.Schema has **no** `InternalsVisibleTo` today. Phase 4 adds Sql.Schema → Sql.Schema.Tests. | P4 |
| C8 | The existing public abstract hot types were not audited. | Each is audited in the phase that touches it (§5.3). | P2, P3, P8 |
| C9 | Test doubles that are both a `Storage` and a record space break under single inheritance. | They are split. Verification found three: `CoordinatorStorage` (`TransactionCoordinatorRecoveryTests.cs:318`), `RollbackStorage` (`TransactionCoordinatorRollbackTests.cs:926`) and `RecordStorage` (`RecordSpaceVersionStoreTests.cs:246`). The first two also name `IStorage` in their base lists and re-implement its members, which breaks in P1, not P2: P1 removes those and moves the hooks to the coordinator (§6.9). Only the record-space split waits for P2. **At P2:** five doubles, not three (two crash-suite doubles came later); all split (§6.9). | P1, P2 |
| C10 | The shared `DatabaseEngineBuilderState.cs` is typed against the root interfaces. The bridge must still satisfy `IDatabaseServer.Context`. | Step P4.0 makes the state generic before the first model PR. The context classes are deleted in P6. **At P4.0:** generic over the engine and, for the bridge, over the product types; the bridge overload that the unadopted builders use reads `server.Context.Engine` (§6.5, §7). | P4.0, P6 |
| C11 | The performance numbers were tagged `[Certain, measured]`, but they do not reproduce. | They are retagged `[Likely]`, given as ranges with ±60% variance, and the "whatever the type shape" claim is dropped (§9). | P0 |
| C12 | Rule conflicts the original rule changes missed: access-modifier rule 1, "internal types are internal", the `Internal/` namespace, one public type per file. | `database-area.md` ("How this relates to the other rules") explains why rule 1 and the checklist line still hold. The moves out of `Internal/`, with their namespace changes and test `using` lines, are counted in P2, P4 and P5 (§10). | P0 |
| C13 | Smaller gaps: the Execution files `QueryPipelineDelegate.cs` and `QueryTransactionStatus.cs`; `EmbeddedDatabase.TryGetEngine`; Studio's casts index a root-typed collection; cohesion-docs; #1236 is two images per page, not one. | All added: rows 15 and 5, §5.2, P7, P8, and §1. | P1, P6, P7, P8 |
| C14 | Some planned surface was wider than needed: the public strategies, `public virtual` observer hooks, and an unexplained provisioning flag. | All five strategies are `internal abstract`. Observer hooks are `protected internal virtual`. `SupportsSchemaProvisioning` is the only capability member on `DatabaseInstance`. | P4, P5, P3 |

Program items added with the critique:

- The four copies of the explicit-transaction state machine (the #1225 follow-up) are
  consolidated in the `DatabaseTransaction` base (§6.4).
- One Diagnostics convention is chosen: never null (§6.8, from #1228).
- #1232 is superseded (D5).

Two defects of the design were found while verifying it for this plan:

- **`EngineLockManager`.** `TransactionCoordinator` wraps the lock manager in a private
  `EngineLockManager` view (`TransactionCoordinator.cs:624`) that defers release-all to the
  transaction manager. That is what keeps #1226's deferred writers holding their locks. A sealed
  `LockManager` cannot be decorated, so the view becomes an internal mode of the type (§6.2).
- **`VersionStore` visibility.** It cannot be `internal abstract`: the public
  `RecordSpaceVersionStore` (`RecordSpaceVersionStore.cs:15`, exposed at
  `TransactionCoordinator.cs:132`) derives from it, and a public class cannot have an internal
  base (CS0060). It is public, with a `private protected` constructor (row 45).

The phase-0 review (2026-10-04) found more. Each is folded in where it lands:

- **Storage sub-components are reachable.** Rows 31, 34, 37 and 38 were marked internal-only on
  a name search. Public `Storage` members return them, and nine shipped assemblies call through
  those members, so the rows become sealed public types in P2 (§5.1).
- **P1 removes the coordinator tests' hooks.** Deleting `IStorage` and `IStorageJournal` removes
  the interception the coordinator tests rely on (eight tests at P1, re-counted in §6.9). The
  hooks move to the coordinator in P1 (§6.9).
- **P3 cannot gate on model behavior.** No model derives from the root bases until P4, so P3
  tests the bases through root-level doubles, and each model's P4 PR carries its own #1188,
  #1225 and #1226 gate (§6.4).
- **The base criteria were incomplete.** Four approved bases have one shipped implementation or
  none. They are inverted seams, a third case the rule now names (§8).
- **The schema-provisioning bridge.** Hosting's type test needs `IDatabaseSchemaProvisioner`
  until P6, so `SqlDatabase` keeps it in its base list until then (row 8).

## 5. Inventory

### 5.1 The 106 public interfaces

[Certain] `rg "interface I\w+" resources/Database --glob '**/src/**'` finds exactly 106
declarations on 2026-10-04. All are public and all are in `Abstractions/`. None are internal or
nested, and no `shared/` folder declares one. The set matches the design inventory row for row.
Paths in the Project column are under `resources/Database/Assimalign.Cohesion.<Project>/src/`.

**How "delete" was checked.** [Certain] A name search cannot see a consumer that reaches a type
through `var` or member access, so every delete row was checked a second way: each public member
whose type is the deleted interface was found (`rg "public .*\bI<Name>\b" --glob '**/src/**'`), and
its member-access consumers were searched outside the owning project, for the storage rows with
`rg "\.(PageManager|FreeSpaceMap|BufferPool)\b|GetUnitIterator\(" resources/Database`. That moved
rows 31, 34, 37 and 38 from delete to sealed: `Storage.PageManager`, `Storage.FreeSpaceMap` and
`Storage.GetUnitIterator` are public (`Storage.cs:113`, `:121`, `:837`, `:843`), and Indexing,
Graph.Catalog, Graph.Storage, Transactions, Sql, Sql.Catalog, KeyValuePair.Catalog,
Documents.Catalog and Blob.Catalog call through them. Making the types internal would need a
shipped-to-shipped `InternalsVisibleTo`. The other delete rows hold:

- row 30's carrier, `Storage.BufferPool` (`Storage.cs:117`), is read only by Storage.Tests
  (`StorageConcurrencyTests.cs:211`, `:338`), so the property becomes internal;
- row 42's carriers are the `TransactionLog` factories (§5.2);
- row 23's carrier is `BTreeIndexManagerOptions.TransactionSource` (§6.3);
- rows 93 to 101 are reached only through `ISqlSchema`, which becomes the opaque `SqlSchema` (§6.7);
- the remaining delete rows have no public carrier.

**Proposal** is one of four values:

- **keep**: the interface stays.
- **delete**: the interface goes and no public type replaces it.
- **sealed**: a public sealed type replaces it.
- **abstract**: an abstract base replaces it. A trailing *(internal)* marks an `internal abstract`
  base.

**Phase** is the phase that changes the row. "P3/P6" means the base arrives in P3 and the
interface is deleted in P6.

| # | Interface | Project | Layer | Proposal | Target | Phase |
|---|---|---|---|---|---|---|
| 1 | `IDatabase` | Database `Abstractions/IDatabase.cs:16` | area root | abstract | `public abstract class DatabaseInstance`, with a protected constructor taking the name, the owning engine and the schema-provisioning capability. `Name` and `Engine` are non-virtual and field-backed, and leaves re-expose `Engine` with `new`. NVI `CreateSessionAsync` calls `CreateSessionCoreAsync`. It absorbs schema provisioning (row 8). **At P3:** landed as planned, with one addition the interface forced: `IDatabase` extends `IDisposable` and `IAsyncDisposable`, so the base owns the disposed flag, and non-virtual `Dispose` and `DisposeAsync` reach `protected abstract DisposeCore` or `DisposeAsyncCore` once. `CreateSessionAsync` checks disposal and the token before its core; each model's offline check stays in its core. | P3/P6 |
| 2 | `IDatabaseApplication` | Database `:19` | area root | keep | Unchanged; `Context` is retyped transitively. The O34 seam. | — |
| 3 | `IDatabaseApplicationBuilder` | Database `:7` | area root | keep | Retyped: `AddEngine(DatabaseEngine)` and `AddEngine(Func<IDatabaseApplicationContext, DatabaseEngine>)`. | P6 |
| 4 | `IDatabaseApplicationContext` | Database `:6` | area root | keep | `Engines` becomes `IReadOnlyList<DatabaseEngine>`, `Servers` becomes `IReadOnlyList<DatabaseServer>`, and `GetEngine` returns `DatabaseEngine`. Adds a static extension `GetEngine<TEngine>(name) where TEngine : DatabaseEngine`. | P6 |
| 5 | `IDatabaseEngine` | Database `:29` | area root | abstract | `public abstract class DatabaseEngine : IAsyncDisposable, IDisposable`, with a protected constructor taking the name and model. `Name`, `Model`, `State`, `Workers` and `Servers` are non-virtual and field-backed. `protected` non-virtual `AttachWorker` and `AttachServer` are refused after `CompleteComposition()` (§6.5). NVI create, open, drop, list and try-get members call `*Core` members. A non-virtual `DisposeAsync` keeps the order servers, then workers, then `DisposeAsyncCore`. Leaves add `public new ValueTask<SqlDatabase> OpenDatabaseAsync(...)` over the base NVI member. **At P3 (re-verified):** the interface had gained `OfflineDatabases` (#1243) after the plan; it is the base's one abstract public member, state the leaf computes (rule 4). `Workers` is typed `IReadOnlyList<DatabaseEngineWorker>` and `Servers` `IReadOnlyList<DatabaseServer>`, published copies replaced on each attach. `AttachWorker` starts the worker's pump thread at once, as every engine did, and carries the checks the engines and `DatabaseEngineBuilderState` made: unique worker names (Sql and KeyValuePair checked them), no product attached twice, a server that fronts this engine. The disposal order is servers (last attached first), then every pump stopped and joined, then the workers (last attached first), then `DisposeAsyncCore`, continuing past failures into one `AggregateException`. The engines' pump frame and state fold (`shared/DatabaseEngineWorkerPump.cs`) moved into the base; the shared copy cannot be deleted in P3, because no model engine derives from the base yet and the root may grant no model its internals, so each model stops compiling it in its P4 PR and the last deletes it. **P3 review:** every NVI member checks the name, then disposal, then the token before its core; `GetDatabasesAsync` makes both checks when it is called (the models made the disposal check at the first `MoveNextAsync`), and the constructor rejects a blank name, which every model's options accept today. §6.4 lists these with the other P4 changes. | P3/P6 |
| 6 | `IDatabaseEngineBuilder` | Database `:7` | area root | delete | Five `public sealed <Model>DatabaseEngineBuilder` types with internal constructors, typed `AddWorker(Func<SqlDatabaseEngine, DatabaseEngineWorker>)` and `AddServer(Func<SqlDatabaseEngine, DatabaseServer>)`, and a `Build()` that returns the model engine. Shared logic moves to `DatabaseEngineBuilderState<TEngine>` (P4.0). **At P4.0 (re-verified):** the state is `DatabaseEngineBuilderState<TEngine, TWorker, TServer>`, not `<TEngine>`. The factories are typed `Func<TEngine, TWorker>` and `Func<TEngine, TServer>`, but the products cannot be fixed to the bases yet: a model that has not adopted them composes `IDatabaseEngineWorker` and `IDatabaseServer` (its servers and its tests' worker and server doubles implement only the interfaces), the one that has composes `DatabaseEngineWorker` and `DatabaseServer`, and during P4 both kinds compile the same shared file. The constraints are `TEngine : class, IDatabaseEngine`, `TWorker : class, IDatabaseEngineWorker` and `TServer : class, IDatabaseServer`, which the bases satisfy until P6. `Complete` takes the leaf's internal compose method (§6.5); a bridge overload over the engine's two attach members serves the builders whose model has not adopted the base. P6 collapses the state to `DatabaseEngineBuilderState<TEngine> where TEngine : DatabaseEngine`, with the products fixed to the bases. **KeyValuePair at P4:** landed as planned. `KeyValueDatabaseEngineBuilder` is public sealed with an internal constructor, its state is `<KeyValueDatabaseEngine, DatabaseEngineWorker, DatabaseServer>`, `AddWorker`/`AddServer` take `Func<KeyValueDatabaseEngine, DatabaseEngineWorker>`/`Func<KeyValueDatabaseEngine, DatabaseServer>` and return the builder, and `Build()` returns the engine through `_state.Complete(engine, engine.Compose)`; it implements no root interface (no Hosting code consumed `IDatabaseEngineBuilder`). **Graph at P4:** landed the same way (row 66): `GraphDatabaseEngineBuilder`, state `<GraphDatabaseEngine, DatabaseEngineWorker, DatabaseServer>`, typed `AddWorker`/`AddServer`, `Build()` through `GraphDatabaseEngine.Compose`. **Documents at P4:** landed the same way (row 62): `DocumentDatabaseEngineBuilder`, state `<DocumentDatabaseEngine, DatabaseEngineWorker, DatabaseServer>`, typed `AddWorker`/`AddServer` (Documents has no wire server, but the builder keeps the server factory every model's builder has), `Build()` through `DocumentDatabaseEngine.Compose`. **Blob at P4:** landed the same way (row 55): `BlobDatabaseEngineBuilder`, state `<BlobDatabaseEngine, DatabaseEngineWorker, DatabaseServer>`, typed `AddWorker`/`AddServer`, `Build()` through `BlobDatabaseEngine.Compose`. **Sql at P4:** landed the same way (row 83): `SqlDatabaseEngineBuilder`, state `<SqlDatabaseEngine, DatabaseEngineWorker, DatabaseServer>`, typed `AddWorker`/`AddServer`, `Build()` through `SqlDatabaseEngine.Compose`. It was the last builder on the bridge, so the Sql PR deleted the bridge overload of `Complete` and its `ThrowIfAttached` helper (§6.5); the state's constraints stay the root interfaces until P6, as planned. | P4.0/P4/P6 |
| 7 | `IDatabaseEngineWorker` | Database `:19` | area root | delete | The existing `DatabaseEngineWorker` (`DatabaseEngineWorker.cs:64`) is already mostly NVI: #1268 and its review landed a non-virtual `Run` (`:146`) and `RunIteration` (`:207`) over `protected abstract void RunIterationCore` (`:230`), with the per-database failure record (`protected` non-virtual `BeginDatabase`, `ReportFailure` and `ReportUnfinished`, `:242-329`), `Fault`, `ConsecutiveFailures`, `FailureCount` and `FailureBackoff`. The review changed the core from `bool` to `void`: a pass reports unfinished work per database (`ReportUnfinished`), so the return value carried nothing. P3 still makes `Name`, `Kind` and `Interval` set by the constructor and non-virtual (abstract today, `:102-108`). The trigger wait (`:350`) stays a `protected virtual` lifecycle hook; the checkpoint, purge and write-ahead flush workers override it (`*WriteAheadFlushWorker.cs:54`). P3 also moves the engines' shared pump and state fold (`shared/DatabaseEngineWorkerPump.cs`, compiled into each model since #1268's review) into the root engine base. **At P3:** landed. `protected DatabaseEngineWorker(string name, DatabaseEngineWorkerKind kind, TimeSpan interval)` rejects a blank name; the interval is not validated there, because the engines validate their options and an invalid one must keep failing the way it does today. The 22 leaves pass their values to the constructor (the shared `DatabaseCheckpointWorker` names itself `{engine}/checkpoint`), so a built-in worker's cadence is captured from the engine's options when the engine is created instead of read on every trigger wait. The pump and fold are in `DatabaseEngine` (row 5); the shared copy stays until P4. **Scheduled by the P3 review:** `DatabaseEngine` disposes a worker through type tests (`is IAsyncDisposable`, `is IDisposable`), kept from the shared pump, because the model engines that compile that pump dispose the same workers (the shared `DatabaseCheckpointWorker` is `IDisposable`) and the model tests' workers implement `IDatabaseEngineWorker, IDisposable` directly. The P4 PR that deletes the shared pump gives the worker base a non-virtual disposal over a `protected virtual DisposeAsyncCore` lifecycle hook (rule 4), moves `DatabaseCheckpointWorker`'s `Dispose` body into it, and replaces the type tests with the call. The hook's entry point should be `internal` to the root, not public: `DatabaseEngine.Workers` is public, and a public `DisposeAsync` would let outside code dispose a worker the engine still pumps. **KeyValuePair at P4:** a single shared file cannot be left out of one consumer's `CohesionSharedSource` (the link is the whole `shared/` folder, and the per-file item is marked not ready), so `shared/DatabaseEngineWorkerPump.cs` is wrapped in `#if !COHESION_DATABASE_ENGINE_PUMP_IN_BASE`, and a model that adopts the engine base defines the constant in its csproj; KeyValuePair does. The last model PR deletes the file and the constant, and the disposal hook above stays scheduled for it: the type tests keep disposing KeyValuePair's checkpointer through `IDisposable`. A test worker that implemented only `IDatabaseEngineWorker` (`KeyValueWorkerResilienceTests`' `EscapingWorker`) cannot be registered through the typed `AddWorker`, so it derives from the base and its first pass throws instead of its loop. **Graph at P4:** the Graph csproj defines the constant too, and the base's type tests dispose Graph's checkpointer (the shared `DatabaseCheckpointWorker`) through `IDisposable`; `GraphWorkerResilienceTests`' `EscapingWorker` moved to the base the same way, and the builder tests' interface-only `RecordingWorker` became a base worker (§6.9). **Documents at P4:** the Documents csproj defines the constant too, and the base's type tests dispose Documents' checkpointer (the shared `DatabaseCheckpointWorker`) through `IDisposable`; `DocumentWorkerResilienceTests`' `EscapingWorker` moved to the base, and the builder tests' interface-only `RecordingWorker` became a base worker (§6.9). **Blob at P4:** the Blob csproj defines the constant too, and the base's type tests dispose Blob's checkpointer (the shared `DatabaseCheckpointWorker`) through `IDisposable`; `BlobWorkerResilienceTests`' `EscapingWorker` moved to the base, and the builder tests' interface-only `RecordingWorker` became a base worker (§6.9). **Sql at P4 (the last model):** `shared/DatabaseEngineWorkerPump.cs` and the four `COHESION_DATABASE_ENGINE_PUMP_IN_BASE` defines (KeyValuePair, Graph, Documents and Blob csprojs) are deleted, and the worker base has its release hook: `protected virtual ValueTask DisposeAsyncCore()`, empty by default (rule 4, a lifecycle hook beside `WaitForTrigger`). The shared `DatabaseCheckpointWorker` overrides it with its former `Dispose` body (its lanes) and no longer implements `IDisposable`, and the engine's `IAsyncDisposable`/`IDisposable` type tests are gone. **As the review applied it, no public entry:** the worker has no public disposal and is neither `IAsyncDisposable` nor `IDisposable`. The shared `DatabaseEngineBuilderState`, compiled into each model assembly, must release a worker it refused or one a failed build left unattached, and the root grants no model its internals; it reaches the release the way it reaches the leaf's compose method. The root engine base has `protected static ValueTask ReleaseUnownedWorkerAsync(DatabaseEngineWorker)`, each model engine re-exposes it as `internal static ReleaseRefusedWorkerAsync`, and each builder passes that to `Complete(engine, engine.Compose, …ReleaseRefusedWorkerAsync)`, whose third parameter (`Func<TWorker, ValueTask>`) releases a rejected worker; a rejected server or engine is still disposed through its public disposal. A worker belongs to one engine: `AttachWorker` claims it before it starts the pump and refuses one that is not free (another engine owns it, or it was released) with `InvalidOperationException`, and a failed thread start returns it to free so the builder can release it. The owning engine's disposal releases it through the internal `ReleaseAsync` after the pumps stop, and `ReleaseUnownedWorkerAsync` does nothing on an owned worker, so code holding `DatabaseEngine.Workers` cannot release a worker at all. The hook runs at most once whichever path reaches it (`DatabaseEngineTests.DisposeAsync_Worker_ShouldBeReleasedOnceByItsOwner`, `AttachWorker_ReleasedWorker_ShouldBeRefused`, `AttachWorker_WorkerAnotherEngineOwns_ShouldBeRefused`). As the implementer first landed it, the worker was `IAsyncDisposable` with a public, ownership-guarded `DisposeAsync`, and `AttachWorker` marked it owned only after the pump started and accepted a worker that was not free; both reviews showed the internal-only shape needs no grant (§7, "Sql, as landed", "Review, as applied"). The model and root tests' `RecordingWorker` doubles count releases in the hook instead of implementing `IDisposable` (§6.9). | P3 (NVI)/P6 |
| 8 | `IDatabaseSchemaProvisioner` | Database `:7` | area root | delete | Folded into `DatabaseInstance`: a non-virtual `public bool SupportsSchemaProvisioning`, set by `protected DatabaseInstance(Name name, DatabaseEngine engine, bool supportsSchemaProvisioning = false)` (rule 6), and an NVI `ApplySchemaAsync` that throws `NotSupportedException` while the flag is `false` and otherwise calls a `protected virtual ApplySchemaCoreAsync` whose default throws `NotSupportedException`. It is the only capability member on `DatabaseInstance`. **Bridge:** Hosting's type test (`Hosting/src/Internal/DefaultDatabaseProvisioner.cs:49`) still needs the interface until P6, and the Sql PR of P4 deletes `ISqlDatabase` (`Sql/src/Abstractions/ISqlDatabase.cs:6`), which is how `SqlDatabase` carries it today. So `SqlDatabase` keeps `IDatabaseSchemaProvisioner` in its base list until P6, implemented by the inherited NVI member. Only Sql claims the interface, as today, and the SampleHost provisioning test stays green. P6 deletes it and turns the type test into a flag check. **At P3:** landed as planned; `DatabaseInstance` does not list `IDatabaseSchemaProvisioner`. `ApplySchemaAsync` checks disposal, a null schema, the capability and the token, in that order, before the core. **Sql at P4:** landed as planned: `SqlDatabase` passes `supportsSchemaProvisioning: true`, overrides `ApplySchemaCoreAsync` (the offline refusal, `COHSQLT004`, then the provisioner) and lists `IDatabaseSchemaProvisioner` after `DatabaseInstance`, implemented by the inherited `ApplySchemaAsync`. Hosting's type test still finds it, and the SampleHost provisioning test passes (§7). | P3/P6 |
| 9 | `IDatabaseServer` | Database `:26` | area root | abstract | `public abstract class DatabaseServer : IAsyncDisposable`, with a protected constructor taking the engine. `Engine` is non-virtual and field-backed (replacing `Context.Engine`), and leaves re-expose it typed with `new`. NVI `StartAsync` and `StopAsync`, with a state guard, call `StartCoreAsync` and `StopCoreAsync`. `public abstract IReadOnlyCollection<DatabaseServerSession> Sessions`. During the bridge, `Context` stays a temporary abstract member (row 10). **At P3 (re-verified):** the state guard is the lifecycle the Sql, KeyValuePair and Graph servers each carried (a lifecycle gate; created inert; a start while running returns; a failed start and any stop are terminal; a start after them throws `ObjectDisposedException`; stop is idempotent and runs for a server that never started, so the leaf releases its listener). `DisposeAsync` is the non-virtual stop. `Context` is public abstract until P6. **Corrected by the P3 review:** Blob's server differs on one path. It refuses a start while its engine is not `Running` ("The Blob engine is {State} and cannot accept sessions.") before it marks itself stopped, so the server stays inert: a later start can retry, and a later stop still disposes `options.Listener`. Under the base every start that throws is terminal and a later stop skips `StopCoreAsync`, so the Blob PR's `StartCoreAsync` disposes the listener before it rethrows that refusal, and adds a test (a start refused while the engine is `Faulted`, then `DisposeAsync` disposes the listener). Retry after a refused start is lost; keeping it would need a non-terminal refusal path in the base, an owner decision (§7, P3 owner questions). **Blob at P4 (re-verified, then landed):** the refusal was as described (`Server/BlobDatabaseServer.cs:104`, inside the server's own lifecycle gate, before it marked itself stopped). `BlobDatabaseServer` is a sealed leaf of the base: `StartCoreAsync` checks the engine's state first and disposes `options.Listener` before it throws the refusal ("The Blob engine is {State} and cannot accept sessions."), as a bind failure already did; `StopCoreAsync` keeps the model's drain of sessions and rejections and its rethrow of an accept-loop failure after the drain. A later start throws `ObjectDisposedException` and a later stop or disposal releases nothing more (`BlobDatabaseServerTests.StartAsync_EngineFaulted_ShouldDisposeTheListenerAndStayStopped`: a `Faulted` engine, the listener disposed once and never bound). The terminal refusal is the interim choice, pending owner confirmation (§7, Blob decision 31). The server's `Engine` is re-exposed typed with `new`, and `Sessions` is the base's abstract getter (the tests read it instead of `Context.Sessions`). **Sql at P4:** `SqlDatabaseServer` is a sealed leaf of the base. Its lifecycle was one of the three the base's guard came from and it refused no start for the engine's state, so no lifecycle behavior changed and interim decision 31 does not reach it: `StartCoreAsync` binds the listener (a bind failure still disposes it before it propagates) and starts the accept loop; `StopCoreAsync` keeps the drain, the hard abort after `ShutdownDrainTimeout` and the listener's release last. `Engine` is re-exposed typed with `new`, `Sessions` overrides the base's getter (the server tests and `SqlClientTests` read it instead of `Context.Sessions`), and `Context` stays for the bridge until P6 (row 10). | P3/P6 |
| 10 | `IDatabaseServerContext` | Database `:16` | area root | delete | `Engine` and `Sessions` fold into `DatabaseServer`. The four context classes (`Blob`, `Graph`, `KeyValuePair`, `Sql` `Internal/*DatabaseServerContext.cs:9`) and five test-double contexts are deleted **in P6**, because Hosting reads `server.Context.Engine` until P6 retypes it. | P6 |
| 11 | `IDatabaseServerSession` | Database `:11` | area root | abstract | `public abstract class DatabaseServerSession`, with a protected constructor taking `(Guid, ProtocolVersion, string? principal)` that backs non-virtual getters. `public abstract DatabaseSession? DatabaseSession`. Leaves stay internal sealed. **Corrected at P3:** a server session exists from accept, before its handshake (`Start` runs the pump), and its four leaves set `ProtocolVersion` during the handshake and `Principal` after authentication. So the protected constructor takes nothing and generates the `Id` (every leaf did `Guid.NewGuid()`), and the version and principal are base fields set once through protected, non-virtual `SetNegotiatedVersion` and `SetAuthenticatedPrincipal` (rule 6). Disposal is a non-virtual `DisposeAsync` over `protected abstract DisposeAsyncCore`. This follows rule 6's second sentence (state changes go through protected, non-virtual methods on the base) but not its list, which names the protocol version and principal among the values fixed at construction. The rule file is binding and changing it is an owner decision recorded in O34a, so P3 does not edit it; the amendment is an open owner question (§7). **Blob at P4:** `BlobDatabaseServerSession` is an internal sealed leaf; its handshake records the negotiated version and the authenticated principal through the base's one-shot setters, as the KeyValuePair and Graph leaves do (the interim choice, pending owner confirmation, §7, Blob decision 30), and its `DatabaseSession` is a covariant `BlobDatabaseSession?` override. **Sql at P4:** `SqlDatabaseServerSession` is an internal sealed leaf on the same terms: the handshake records the version and the principal through the one-shot setters (interim decision 30), and its `DatabaseSession` is a covariant `SqlDatabaseSession?` override. | P3/P6 |
| 12 | `IDatabaseSession` | Database `:19` | area root | abstract | `public abstract class DatabaseSession : IAsyncDisposable`. The base owns `State`, `CurrentTransaction` and the one "already active" check, with one message (§6.4). `Database` is non-virtual and field-backed, and leaves re-expose it typed with `new`. NVI `BeginTransactionAsync` and `ExecuteAsync` call `BeginTransactionCoreAsync(IsolationLevel)` and `ExecuteCoreAsync`. **At P3:** landed (§6.4). `Database` is typed `DatabaseInstance`; both `ExecuteAsync` overloads have a core. The session also owns the reservation of a BEGIN in flight and, for models whose sessions run one operation at a time, a protected operation hold (`TryEnterOperation`/`ExitOperation`) that the "already active" check reads, and its teardown order: the leaf's `DisposeAsyncCore` ends its running operations, then the base ends the open transaction. | P3/P6 |
| 13 | `IDatabaseTransaction` | Database `:18` | area root | abstract | `public abstract class DatabaseTransaction : IAsyncDisposable`, with a protected constructor taking `(TransactionId, IsolationLevel)`. It owns the explicit-transaction state machine (§6.4). NVI `CommitAsync` and `RollbackAsync` call `CommitCoreAsync` and `RollbackCoreAsync`. A non-virtual `DisposeAsync` rolls back if the transaction is active, then calls `DisposeAsyncCore`. **At P3:** landed (§6.4). The cores take no token: the base observes it only before an end starts. The leaf's other vocabulary is `GetKernelState`, `GetOfflineRefusal` and `CreateAbortedException`; `DisposeAsyncCore` is a `protected virtual` lifecycle hook with an empty default, run once. | P3/P6 |
| 14 | `IQueryExecutor` | Execution `:13` | child root | delete | `SqlQueryExecutor` stays internal sealed. **At P1:** its public `ExecuteAsync(QueryRequest, CancellationToken)`, which only threw `NotSupportedException` to satisfy the interface, went with it. | P1 |
| 15 | `IQueryPipeline` | Execution `:18` | child root | delete | Deleted together with `QueryPipelineBuilder`, `QueryExecutionContext`, `Internal/BuiltQueryPipeline`, `QueryPipelineDelegate`, `QueryTransactionStatus`, `QueryStatementResult` and `tests/QueryPipelineTests.cs`. `QueryStatementResult` (`QueryStatementResult.cs:12`) came with the pipeline, and its only constructions are in `QueryPipelineTests.cs:93` and `:272`. This is the Web-style tap-in pipeline the owner ruled out, and no engine consumes it. **At P1:** `Exceptions/QueryExecutionException` went too: its documented scope was "pipeline composition, stage contract violations", and nothing threw it. The Execution test project stays, with a `QueryRequestTests` suite over the surviving request contract instead of an empty shell. | P1 |
| 16 | `IQueryPipelineStage` | Execution `:11` | child root | delete | Deleted with the pipeline. | P1 |
| 17 | `IQueryTransactionScope` | Execution `:15` | child root | delete | Deleted with the pipeline. | P1 |
| 18 | `IResourceGovernor` | Governance `:9` | child root | delete | The interface and the `Database.Governance` project are deleted (D6). | P1 |
| 19 | `IIndex` | Indexing `:28` | child root | sealed | `public sealed class BTreeIndex`, with an internal constructor; `BTreeIndexManager` creates it. **At P2:** landed, promoted from the internal class and moved out of `Internal/`. | P2 |
| 20 | `IIndexCursor` | Indexing `:10` | child root | sealed | `BTreeCursor`: public sealed if a public `BTreeIndex` member returns it, otherwise internal sealed. **At P2:** public sealed: both `BTreeIndex.OpenCursor` overloads return it. It keeps `IAsyncDisposable`, whose `DisposeAsync` releases nothing (the cursor materializes its range when it opens), so that a cursor that pins pages later changes no call site. | P2 |
| 21 | `IIndexManager` | Indexing `:14` | child root | sealed | One `public sealed class BTreeIndexManager` for the manager and the registry. It absorbs `public static class BTreeIndexManager` (`BTreeIndexManager.cs:13`, `Create` at `:38`). **At P2:** landed; the internal `DefaultIndexManager` was folded in, and the constructor is private behind `Create`. `CreateIndexAsync`, `TryGetIndex` and `GetIndexes` return `BTreeIndex`. | P2 |
| 22 | `IIndexRegistry` | Indexing `:17` | child root | sealed | Merged into `BTreeIndexManager` (row 21). **At P2:** landed; the 23 `IIndexRegistry` casts in Sql, KeyValuePair, Documents.Catalog, Graph.Storage and their tests became direct `ExportRegistrations()` calls. | P2 |
| 23 | `IStorageTransactionSource` | Indexing `:16` | child root | delete | `BTreeIndexManagerOptions` takes a per-engine delegate, `Func<TransactionContext, StorageTransaction>` (§6.3). **At P2:** landed as `required Func<TransactionContext, StorageTransaction> TransactionSource` (the property kept its name). Each engine passes a private `ResolveStatementBracket` method that replaced its wrapper class with the same message (§6.3). | P2 |
| 24 | `IProtocolFrameReader` | Protocol `:10` | child root | abstract | `public abstract class ProtocolFrameReader : IAsyncDisposable`, with a protected constructor, because its leaves live in Protocol, Database.Client and Blob.Client. NVI `ReadFrameAsync` calls `ReadFrameCoreAsync`, and a non-virtual `DisposeAsync` calls `DisposeAsyncCore` (the interface extends `IAsyncDisposable` today, `IProtocolFrameReader.cs:10`). `public static ProtocolFrameReader Create(Stream, bool leaveOpen = false)` replaces `ProtocolFraming.CreateReader`. **Landed at P2 (re-verified):** four leaves, as counted: the internal stream reader, `ProtocolChannel`'s private family reader, Database.Client's `ClientFrameReader` and Blob.Client's private error reader. `DisposeAsyncCore` is a `protected virtual` lifecycle hook with an empty default, which Blob's view over the pooled reader keeps. The NVI members add no check, so the frame path behaves as before: the interfaces carried none, the reader's one argument is the token its stream leaf already passes to every `Stream.ReadAsync`, and the writer's one frame check stayed in its stream leaf until decision 29 of 2026-10-06 moved it into the base's `WriteFrameAsync` (row 25). Row 27's base checks are the track's only behaviour change. Public carriers retyped with it: `ProtocolChannel.Reader`, the parameters of Database.Client's `IDatabaseProtocolExchange<TResult>.ExecuteAsync` and `IDatabaseStreamingExchange.OpenAsync`/`CopyToAsync` (the interfaces themselves go in P5, rows 51 and 52), and `BlobProtocolTransfer`'s three frame-endpoint overloads. | P2 |
| 25 | `IProtocolFrameWriter` | Protocol `:10` | child root | abstract | `public abstract class ProtocolFrameWriter : IAsyncDisposable`. NVI `WriteFrameAsync` calls `WriteFrameCoreAsync`, and `DisposeAsync` calls `DisposeAsyncCore`. `Create(Stream, bool)` replaces `ProtocolFraming.CreateWriter`. **Landed at P2 (re-verified):** the interface also had `FlushAsync`, which the plan missed; it becomes an NVI member over `protected abstract FlushCoreAsync`. Four leaves, mirroring row 24. The payload bound (`ProtocolFrameHeader.MaxPayloadLength`) stays in the stream writer's core, the one writer that encodes the envelope; in the base it would run before `ProtocolChannel`'s family check and change which error a frame failing both reports. `ProtocolChannel.Writer` and the carriers of row 24 are retyped. **Landed per the 2026-10-06 decision (29):** the bound moved into the base's public non-virtual `WriteFrameAsync` (rule 4), which throws the `ProtocolException` synchronously before any core runs, so every writer refuses an oversized payload the same way, a leaf that never forwards to the stream writer included; the stream writer's core lost its copy. The error order changed as predicted and is pinned: a frame failing both the bound and the family check reports the bound, a frame failing only the family check reports the family (`ProtocolFramingTests`, two tests; Protocol.Tests 21 → 23; Protocol `DESIGN.md`). | P2 |
| 26 | `IAuthorizationService` | Security `:9` | child root | delete | It has no implementer anywhere. | P1 |
| 27 | `IDatabaseAuthenticator` | Security `:18` | child root | abstract | `public abstract class DatabaseAuthenticator`, with a protected constructor. NVI `AuthenticateAsync` calls `AuthenticateCoreAsync`. It absorbs `public static class DatabaseAuthenticator` (`DatabaseAuthenticator.cs:8`) as `public static DatabaseAuthenticator AllowAll`. **Landed at P2 (re-verified):** `AuthenticateAsync` rejects a null database or principal (`ArgumentNullException`) and a canceled token before the core runs. The cancellation check moved out of the internal `AllowAllDatabaseAuthenticator`, which already made it, so the shipped path is unchanged; the servers pass decoded wire strings, which are never null. Public carriers retyped: the four servers' `Authenticator` option (`Sql`, `KeyValue`, `Graph` and `Blob` `DatabaseServerOptions`); Studio sets it to `null` and compiles unchanged. A new Database.Security test project pins the base's checks (Security had none). | P2 |
| 28 | `IStorage` | Storage `:16` | child root | delete | Consumers retype to the existing abstract `Storage`. The default member `EnsureCommitDurable` (`IStorage.cs:218`) already exists as `Storage.EnsureCommitDurable` (`Storage.cs:219`), and the explicit implementation (`Storage.cs:241`) goes. Two Transactions.Tests doubles re-implement `IStorage.Checkpoint` and `ReserveTransactionSequence`, which are non-virtual on `Storage` (`:549`, `:467`); their hooks move to the coordinator in P1 (§6.9). | P1 |
| 29 | `IStorageBackupManager` | Storage `:9` | child root | delete | It has no implementer and no reference. | P1 |
| 30 | `IStorageBufferPool` | Storage `:15` | child root | delete | `StorageBufferPool` stays internal sealed, and the public `Storage.BufferPool` (`Storage.cs:117`) becomes internal. Only Storage.Tests reads it (`StorageConcurrencyTests.cs:211`, `:338`), through Storage's existing grant. | P1 |
| 31 | `IStorageFreeSpaceMap` | Storage `:15` | child root | sealed | `public sealed class StorageFreeSpaceMap`, with an internal constructor. The public `Storage.FreeSpaceMap` (`Storage.cs:121`) returns it, and Graph.Catalog and Graph.Storage call `IsAllocated` through it (`Internal/DefaultGraphCatalog.cs:298`, `Internal/DefaultGraphStore.cs:234`). **At P2:** landed as planned, moved out of `Internal/`; its public members are the interface's, unchanged. | P2 |
| 32 | `IStorageJournal` | Storage `:31` | child root | delete | Consumers retype to the existing abstract `StorageJournal`. `FaultInjectingJournal` (`TransactionCoordinatorRollbackTests.cs:844`) decorates the interface to reject rollback records; `StorageJournal.AppendRollback` is non-virtual (`StorageJournal.cs:78`), so the double is deleted and its fault moves to the coordinator in P1 (§6.9). `StorageJournal` itself is audited in P2 (§5.3). | P1 |
| 33 | `IStoragePageHandle` | Storage `:26` | child root | sealed | `public sealed class StoragePageHandle`, with an internal constructor. It is on the per-page hot path. **At P2:** landed as planned. | P2 |
| 34 | `IStoragePageManager` | Storage `:22` | child root | sealed | `public sealed class StoragePageManager`, with an internal constructor. The public `Storage.PageManager` (`Storage.cs:113`) returns it. Indexing's `BTreeIndex` calls `PageManager.GetPage` on the per-page hot path (`Internal/BTreeIndex.cs:104` and seven more sites), as do Graph.Catalog and Graph.Storage, so every call becomes non-virtual. **At P2:** landed, and the type is no longer disposable: the interface extended `IAsyncDisposable` and `IDisposable`, but both members were empty and the storage owns the manager, so a public `Dispose` only invited callers to "dispose" a component they do not own. The storage's two no-op calls and five Storage.Tests `using` declarations went with them. | P2 |
| 35 | `IStorageRecoveryManager` | Storage `:9` | child root | delete | It has no implementer and no reference. | P1 |
| 36 | `IStorageTransaction` | Storage `:30` | child root | sealed | `public sealed class StorageTransaction`, with an internal constructor. **At P2:** landed as planned. `Storage`'s argument check lost its type test (`is not StorageTransaction`), which a sealed parameter type can no longer fail; it never checked that the transaction belongs to the same storage instance, and still does not (no behavior change; §12). | P2 |
| 37 | `IStorageUnit` | Storage `:19` | child root | sealed | `public readonly struct StorageUnit` (internal today, `Internal/StorageUnit.cs:8`), with an internal constructor. The iterator returns it unboxed. **At P2:** landed as planned. | P2 |
| 38 | `IStorageUnitIterator` | Storage `:10` | child root | sealed | `public sealed class StorageUnitIterator : IEnumerator<StorageUnit>`, with an internal constructor. The public `Storage.GetUnitIterator` (`Storage.cs:837`, `:843`) returns it to Transactions, Sql, the five model catalogs and Graph.Storage. **At P2:** landed; `Next(out StorageUnit)` returns the default unit, not `null`, when the scan is exhausted, and `Current` reads the default unit outside a scan. No caller read either case. | P2 |
| 39 | `ILockManager` | Transactions `:16` | child root | sealed | `public sealed class LockManager`, with an internal constructor. It absorbs `public static class LockManager` (`LockManager.cs:8`) as `public static LockManager Create()`. The coordinator's view becomes an internal release filter (§6.2). **At P2:** landed; the internal `DefaultLockManager` was folded in. The view had grown since the plan: #1268 gave it the offline abandonment of waits (`AbandonLockWaits`), so the internal mode carries both (§6.2). | P2 |
| 40 | `IRecordVersionIndex` | Transactions `:13` | child root | abstract | `public abstract class RecordVersionIndex` in Transactions, with a protected constructor, because its leaves live in Indexing, Documents.Catalog and Graph.Storage. NVI erase and clear-deleter members call `protected abstract` cores. Indexing's sealed type (`Indexing/src/RecordVersionIndex.cs:17`) is renamed `BTreeRecordVersionIndex`. **At P2:** landed; the public members check for a null bracket. The rename lands in the Transactions commit, not the Indexing one: Sql's and KeyValuePair's executors import both namespaces, so the two `RecordVersionIndex` types cannot coexist for one commit. `BTreeRecordVersionIndex` keeps its public constructor over a `BTreeIndex`, on the same terms as the journal's (§5.3): it was public API on Indexing's former `RecordVersionIndex`, and Sql's and KeyValuePair's executors construct it. Owner question at the P2 merge: keep the public constructors, or make both internal behind `Create` factories (rule 1). **Landed per the 2026-10-06 decision (27):** the constructor is private behind `public static BTreeRecordVersionIndex Create(BTreeIndex)`, which refuses a null index; the two callers (`SqlPlanExecutor`'s `SqlLiveIndex.Versions`, `KeyValueOperationExecutor`) call it, and `BTreeRecordVersionIndexTests` (two tests; Indexing.Tests 75 → 77) pins the factory and the binding's erase and clear-deleter through the tree. | P2 |
| 41 | `ITransactionContext` | Transactions `:14` | child root | sealed | `public sealed class TransactionContext`, with an internal constructor, plus `public TransactionContext PinStatementSnapshot()` (§6.1). It is the most-consumed kernel contract (47 `src` files), and every call becomes non-virtual. **At P2:** landed, promoted from the internal `DefaultTransactionContext`; 69 `.cs` files retyped across the tree. | P2 |
| 42 | `ITransactionLog` | Transactions `:17` | child root | delete | `internal abstract class TransactionLog` in `Internal/` (rule 11). `public static class TransactionLog` (`TransactionLog.cs:12`) is deleted and its factories become internal (§5.2). | P1 |
| 43 | `ITransactionManager` | Transactions `:17` | child root | sealed | `public sealed class TransactionManager`, promoted from `DefaultTransactionManager`, with an internal constructor. It gets a public static `Create`, an internal overload, and stays exposed by the coordinator (§5.2, C1). | P1 (P2 retypes the parameters) |
| 44 | `ITransactionRecordSpace` | Transactions `:16` | child root | abstract | `public abstract class TransactionRecordSpace`, with a protected constructor, because its leaves live in Sql, KeyValuePair, Graph.Storage, Documents.Storage and Blob.Storage. NVI read, update and delete. **At P2:** landed; location packing and unpacking are NVI too, and update and delete check for a null bracket. | P2 |
| 45 | `IVersionStore` | Transactions `:17` | child root | abstract | `public abstract class VersionStore`, with a `private protected` constructor, because both leaves (`RecordSpaceVersionStore` and `Internal/InMemoryVersionStore`) live in Transactions. It absorbs `public static class VersionStore` (`VersionStore.cs:8`) as `public static VersionStore CreateInMemory()`. **At P2:** landed. The base's public members carry the checks both leaves made (a canceled token on append and read, a null snapshot on read); prune and purge check nothing in the base, because the two stores observe cancellation at different points. | P2 |
| 46 | `IReplicationCoordinator` | Replication (unshipped) | other | delete | The project is deleted with the other Replication placeholders (D6). | P1 |
| 47 | `ISqlReplicationSource` | Sql.Replication (unshipped) | model child | delete | The project is deleted. | P1 |
| 48 | `IDatabaseClient` | Client `:17` | client | sealed | `public sealed class DatabaseClient`. It absorbs the static class (`DatabaseClient.cs:10`). | P5 |
| 49 | `IDatabaseCommandClient` | Client `:8` | client | sealed | `public sealed class DatabaseCommandClient`. It absorbs the static class (`DatabaseCommandClient.cs:9`). | P5 |
| 50 | `IDatabaseConnection` | Client `:19` | client | sealed | `public sealed class DatabaseConnection`, with an internal constructor. `ExecuteAsync<TResult>` (`IDatabaseConnection.cs:73`) is a generic virtual call today, and becomes a non-virtual generic method. | P5 |
| 51 | `IDatabaseProtocolExchange<TResult>` | Client `:12` | client | abstract | `public abstract class DatabaseProtocolExchange<TResult>`, with a protected constructor. The NVI `ExecuteAsync` resets the base-owned `IsResponseComplete` and then calls `ExecuteCoreAsync`; a leaf reports completion through a protected method. This replaces the default interface member (`:24`) and the three hand-written resets in the Sql, KeyValuePair and Graph exchanges. | P5 |
| 52 | `IDatabaseStreamingExchange` | Client `:16` | client | abstract | `public abstract class DatabaseStreamingExchange`, with a protected constructor and NVI. | P5 |
| 53 | `IBlobContainer` | Blob `:18` | model | sealed | `public sealed class BlobContainer`, with an internal constructor. **At P4 (re-verified, then landed):** promoted from `Internal/BlobContainer.cs` to `src/BlobContainer.cs` in the `RootNamespace`, with the interface's documentation on its members and their exceptions; the internal `BlobGuardedStream` that file also declared moved to its own file in `Internal/`, unchanged. `GetOwnershipAsync` is a member of the type (§5.2). Read with the code: a container is bound to the session that returned it (each of its operations is an operation of that session) and unbound when the database returned it (each runs in autocommit), which the type keeps; its operations take no session parameter, so no typed parameter changed. | P4 |
| 54 | `IBlobDatabase` | Blob `:11` | model | sealed | `public sealed class BlobDatabase : DatabaseInstance`, with option B (§6.6). `BlobSessionDatabase` (`Internal/BlobDatabaseSession.cs:216`) is deleted. **At P4 (re-verified, then landed):** promoted from `Internal/BlobDatabaseInstance.cs` to `src/BlobDatabase.cs` in the `RootNamespace`, with an internal constructor that takes the typed engine (the former one took `IDatabaseEngine` and type-tested it for the engine's options); the base owns the name, the engine (re-exposed with `new` over a typed field) and the disposed flag, and the model's `Dispose`/`DisposeAsync` became `DisposeCore`/`DisposeAsyncCore` (the async one now awaits the coordinator's and the storage's `DisposeAsync`). `CreateSessionAsync` is a typed `new` member over the base's NVI member (offline refusal in the core). The interface's four container operations stay on the database and run in autocommit outside any session, as they did (interim decision 32, pending owner confirmation, §7); `BlobSessionDatabase` was at `:223`, not `:216`, and is deleted, and the session exposes the four session-bound ones (option B, §6.6). The model's session and transaction became public sealed leaves too (`BlobDatabaseSession : DatabaseSession`, `BlobDatabaseTransaction : DatabaseTransaction`, rows 12 and 13), its server derives from `DatabaseServer` (row 9), and its server session is an internal sealed `DatabaseServerSession` leaf (row 11). | P4 |
| 55 | `IBlobDatabaseEngineBuilder` | Blob `:10` | model | sealed | `public sealed class BlobDatabaseEngineBuilder`, with an internal constructor and typed `AddServer` and `AddWorker`. **At P4:** landed with the typed factories of row 6, moved out of `Internal/`, with the deviation marker (§8). Its `StorageStrategy` became internal with the option's (row 56). The engine, builder, options, strategy and four workers were, before this PR, the Documents engine's pre-P4 files with the model's names (a diff with the names mapped differs only in namespaces and the verb's name), so they follow the Documents landing with them (§6.5). | P4 |
| 56 | `IBlobStorageStrategy` | Blob `:10` | model | abstract *(internal)* | `internal abstract class BlobStorageStrategy`. The option and builder properties become internal; it is a test seam through the existing grant. **At P4:** landed in `Internal/` (rule 11), with `public abstract` members like the other models' strategies; the option's and the builder's `StorageStrategy` became internal. It has no shipped leaf (the engine's own file-set and in-memory storage are not strategies), so the base is the test seam D9 kept: the fault-injecting double (durable or not) and the builder tests' recording strategy derive through the existing Blob → Blob.Tests grant. | P4 |
| 57 | `IBlobCatalog` | Blob.Catalog `:16` | model child | sealed | `public sealed class BlobCatalog`. It absorbs the static class (`BlobCatalog.cs:10`, `Open` at `:18`). **At P4 (re-verified, then landed):** the internal `DefaultBlobCatalog` was folded in as `BlobCatalog.cs`, with the interface's documentation on its public members; the constructor is private behind `Open`, and the internal `CatalogRecord` it declared moved to its own file in `Internal/`, which keeps it and the codec. The project's `Abstractions/` folder is gone. Read with the code: every member already checked its reference arguments, so no behavior changed (unlike the Documents catalog's index members, row 64). Blob.Catalog has no `InternalsVisibleTo` and needed none. The stale-page handling of `Find` (#1287) is untouched. | P4 |
| 58 | `IBlobClient` | Blob.Client `:10` | client | sealed | `public sealed class BlobClient`. It absorbs the static class (`BlobClient.cs:8`). | P5 |
| 59 | `IBlobConnection` | Blob.Client `:15` | client | sealed | `public sealed class BlobConnection`, with an internal constructor. | P5 |
| 60 | `IDocumentCollection` | Documents `:16` | model | sealed | `public sealed class DocumentCollection`, with an internal constructor. **At P4 (re-verified, then landed):** promoted from `Internal/DocumentCollection.cs` to `src/DocumentCollection.cs` in the `RootNamespace`, with the interface's documentation on its members. `GetAsync`, `PutAsync` and `DeleteAsync` take `DocumentDatabaseSession` instead of `IDatabaseSession`, so a session of another model no longer compiles; a null session stays an `ArgumentNullException`, and a session of another database, or another session than the one a session-bound collection belongs to, stays the model's `DatabaseException` (`DocumentDatabaseScopeTests`). Read with the code: a collection is bound to the session that returned it and unbound when the database returned it, which the type keeps. | P4 |
| 61 | `IDocumentDatabase` | Documents `:10` | model | sealed | `public sealed class DocumentDatabase : DatabaseInstance`, with option B (§6.6). `DocumentSessionDatabase` (`Internal/DocumentDatabaseSession.cs:264`) is deleted. **At P4 (re-verified, then landed):** promoted from `Internal/DocumentDatabaseInstance.cs` to `src/DocumentDatabase.cs` in the `RootNamespace`, with an internal constructor that takes the typed engine; the base owns the name, the engine (re-exposed with `new` over a typed field) and the disposed flag, and the model's `Dispose`/`DisposeAsync` became `DisposeCore`/`DisposeAsyncCore`. `CreateSessionAsync` is a typed `new` member over the base's NVI member (offline refusal in the core). The interface's four collection operations stay on the database and run in autocommit outside any session, as they did; `DocumentSessionDatabase` was at `:271`, not `:264`, and is deleted, and the session exposes the four session-bound ones (option B, §6.6). The model's session and transaction became public sealed leaves too (`DocumentDatabaseSession : DatabaseSession`, `DocumentDatabaseTransaction : DatabaseTransaction`, rows 12 and 13). Documents has no wire server, so rows 9, 10 and 11 have no Documents leaf. | P4 |
| 62 | `IDocumentDatabaseEngineBuilder` | Documents `:10` | model | sealed | `public sealed class DocumentDatabaseEngineBuilder`, with an internal constructor. **At P4:** landed with the typed factories of row 6, moved out of `Internal/`, with the deviation marker (§8). Its `StorageStrategy` became internal with the option's (row 63). | P4 |
| 63 | `IDocumentStorageStrategy` | Documents `:10` | model | abstract *(internal)* | `internal abstract class DocumentStorageStrategy`, with an internal option property. **At P4:** landed in `Internal/` (rule 11), with `public abstract` members like the KeyValuePair and Graph strategies'; the option's and the builder's `StorageStrategy` became internal. It has no shipped leaf (the engine's own file-set and in-memory storage are not strategies), so the base is the test seam D9 kept: the fault-injecting double and the builder tests' recording strategy derive through the existing Documents → Documents.Tests grant. | P4 |
| 64 | `IDocumentCatalog` | Documents.Catalog `:16` | model child | sealed | `public sealed class DocumentCatalog`. It absorbs the static class (`DocumentCatalog.cs:11`). **At P4 (re-verified, then landed):** the internal `DefaultDocumentCatalog` (two partial files) was folded in as `DocumentCatalog.cs` and `DocumentCatalog.Indexes.cs`, with the interface's documentation on its public members; the constructor is private behind `Open`, `EnsureIndexFormat` stays a public static on the type, and the internal `CatalogRecord` it declared moved to its own file in `Internal/`, which keeps it and the codecs. The project's `Abstractions/` folder is gone. Read with the code: three index members dereferenced a null transaction or snapshot (or looked a null index name up and reported it missing) where every other member checked its arguments, so `CreateIndexAsync`, `DeleteIndexAsync` and `SearchIndexAsync` now throw `ArgumentNullException`, as the Graph review did for `GraphStore` (`DocumentCatalogTests`). Documents.Catalog has no `InternalsVisibleTo` and needed none. The stale-page handling of `Find` (#1287) is untouched. | P4 |
| 65 | `IGraphDatabase` | Graph `:12` | model | sealed | `public sealed partial class GraphDatabase : DatabaseInstance`. **At P4 (re-verified, then landed):** promoted from `Internal/GraphDatabaseInstance.cs` and its `.Lifetime.cs` partial to `src/GraphDatabase.cs` and `src/GraphDatabase.Lifetime.cs` in the `RootNamespace`, with an internal constructor that takes the typed engine; the base owns the name, the engine (re-exposed with `new` over a typed field) and the disposed flag, and the model's `Dispose`/`DisposeAsync` became `DisposeCore`/`DisposeAsyncCore`. `CreateSessionAsync` is a typed `new` member over the base's NVI member (offline refusal in the core). The six typed operations (`CreateNodeAsync`, `GetNodeAsync`, `DeleteNodeAsync`, `CreateRelationshipAsync`, `DeleteRelationshipAsync`, `TraverseAsync`) take `GraphDatabaseSession` instead of `IDatabaseSession`, so a null session is an `ArgumentNullException` where it was `COHDBG005` (§6.4). The model's session and transaction became public sealed leaves too (`GraphDatabaseSession : DatabaseSession`, `GraphDatabaseTransaction : DatabaseTransaction`, rows 12 and 13), its server derives from `DatabaseServer` (row 9), and its server session is an internal sealed `DatabaseServerSession` leaf (row 11). | P4 |
| 66 | `IGraphDatabaseEngineBuilder` | Graph `:10` | model | sealed | `public sealed class GraphDatabaseEngineBuilder`, with an internal constructor. **At P4:** landed with the typed factories of row 6, moved out of `Internal/`, with the deviation marker (§8). Its `StorageStrategy` became internal with the option's (row 68). | P4 |
| 67 | `IGraphSchema` | Graph `:9` | model | sealed | `public sealed class GraphSchema`. It absorbs the static class (`GraphSchema.cs:6`) with `Open(GraphDatabase, DatabaseSession)`. **At P4 (re-verified, then landed):** it also absorbs the internal implementation, `Internal/GraphSchemaSession.cs`, behind a private constructor, and `Open` takes the typed session, `Open(GraphDatabase, GraphDatabaseSession)`: the model's typed operations take that session, and a root-typed one would need the cast the program removes. A null database or session is an `ArgumentNullException` where it was `COHDBG005` (§6.4). | P4 |
| 68 | `IGraphStorageStrategy` | Graph `:10` | model | abstract *(internal)* | `internal abstract class GraphStorageStrategy`. The fault-injection double derives through the test grant. **At P4:** landed in `Internal/` (rule 11), with `public abstract` members like `KeyValueStorageStrategy`'s; the option's and the builder's `StorageStrategy` became internal. It has no shipped leaf (the engine's own file-set and in-memory storage are not strategies), so the base is the test seam D9 kept: the fault-injecting double and the builder tests' recording strategy derive through the existing Graph → Graph.Tests grant. | P4 |
| 69 | `IGraphCatalog` | Graph.Catalog `:15` | model child | sealed | `public sealed class GraphCatalog`. It absorbs the static class (`GraphCatalog.cs:10`). **At P4 (re-verified, then landed):** the internal `DefaultGraphCatalog` was folded in, with the interface's documentation on its public members; the constructor is private behind `Open`, and the internal `CatalogRecord` it declared moved to its own file in `Internal/`, which keeps it and the codec. The project's `Abstractions/` folder is gone. Graph.Catalog has no `InternalsVisibleTo` and needed none: its tests use `Open` and the public members. | P4 |
| 70 | `IGraphStore` | Graph.Storage `:9` | model child | sealed | `public sealed class GraphStore`. It absorbs the static class (`GraphStore.cs:10`). **At P4 (re-verified, then landed):** the internal `DefaultGraphStore` (two partial files) was folded in as `GraphStore.cs` and `GraphStore.Indexes.cs`, with the interface's documentation; the constructor is private behind `Open`, and `EnsureIndexFormat` stays a public static on the type. The project's `Abstractions/` folder is gone (`Internal/` keeps the record codec). | P4 |
| 71 | `IGraphClient` | Graph.Client `:10` | client | sealed | `public sealed class GraphClient`. It absorbs the static class (`GraphClient.cs:9`). | P5 |
| 72 | `IGraphConnection` | Graph.Client `:11` | client | sealed | `public sealed class GraphConnection`, with an internal constructor. | P5 |
| 73 | `IKeyValueDatabase` | KeyValuePair `:23` | model | sealed | `public sealed class KeyValueDatabase : DatabaseInstance`. **At P4 (re-verified, then landed):** promoted from `Internal/KeyValueDatabaseInstance.cs` to `src/KeyValueDatabase.cs` in the `RootNamespace`, with an internal constructor that takes the typed engine; the base owns the name, the engine (re-exposed with `new` over a typed field) and the disposed flag, and the model's `Dispose`/`DisposeAsync` became `DisposeCore`/`DisposeAsyncCore`. `CreateSessionAsync` is a typed `new` member over the base's NVI member (offline refusal in the core). The five typed operations (`GetAsync`, `PutAsync`, `TryDeleteAsync`, `ExistsAsync`, `ScanAsync`) take `KeyValueDatabaseSession` instead of `IDatabaseSession`. The model's session and transaction became public sealed leaves too (`KeyValueDatabaseSession : DatabaseSession`, `KeyValueDatabaseTransaction : DatabaseTransaction`, rows 12 and 13), its server derives from `DatabaseServer` (row 9), and its server session is an internal sealed `DatabaseServerSession` leaf (row 11). | P4 |
| 74 | `IKeyValueDatabaseEngineBuilder` | KeyValuePair `:9` | model | sealed | `public sealed class KeyValueDatabaseEngineBuilder`, with an internal constructor. **At P4:** landed with the typed factories of row 6, moved out of `Internal/`, with the deviation marker (§8). | P4 |
| 75 | `IKeyValueStorageStrategy` | KeyValuePair `:13` | model | abstract *(internal)* | `internal abstract class KeyValueStorageStrategy`. It has two real variants plus the durability doubles, which derive through the existing grant. The option property becomes internal. **At P4:** landed in `Internal/` (rule 11), with `public abstract` members like `TransactionLog`'s; the builder's `StorageStrategy` became internal with the option's. The two test strategies (`FaultInjectingJournalStorageStrategy`, `KeyValueStorageDurabilityPolicyTests`' `NonDurableStorageStrategy`) override the members. | P4 |
| 76 | `IKeyValueCatalog` | KeyValuePair.Catalog `:27` | model child | sealed | `public sealed class KeyValueCatalog`. It absorbs the static class (`KeyValueCatalog.cs:12`), and `CaptureSnapshot` (`:41`) becomes an instance method. **At P4:** landed; the internal `DefaultKeyValueCatalog` was folded in, the constructor is private behind `Open`, and the project's `Abstractions/` and `Internal/` folders are gone. | P4 |
| 77 | `IKeyValueCatalogSnapshot` | KeyValuePair.Catalog `:14` | model child | sealed | `public sealed class KeyValueCatalogSnapshot`, with an internal constructor. **At P4:** landed as a class, not the former internal positional record, which would expose a public constructor and `with` (the row 81 reasoning). | P4 |
| 78 | `IKeyValueClient` | KeyValuePair.Client `:20` | client | sealed | `public sealed class KeyValueClient`. It absorbs the static class (`KeyValueClient.cs:11`). | P5 |
| 79 | `IKeyValueClientObserver` | KeyValuePair.Client `:18` | client | abstract | `public abstract class KeyValueClientObserver`, with a protected constructor. Its hooks are `protected internal virtual` with empty bodies. | P5 |
| 80 | `IKeyValueConnection` | KeyValuePair.Client `:24` | client | sealed | `public sealed class KeyValueConnection`, with an internal constructor. | P5 |
| 81 | `ISqlAggregateExpression` | Sql `:9` | model | sealed | `public sealed class SqlAggregateExpression`, with get-only `SourceType`, `Selector` and `Predicate` and an internal constructor. Not a record: the current type is a positional record (`Internal/SqlAggregateExpression.cs:9`), and a public one would expose a public `with` that clones around validation (C7). `Sql.Sum` (`Sql.cs:23`) returns it. **At P4 (re-verified, then landed):** the interface was `Abstractions/ISqlAggregateExpression.cs` over the internal positional record, as listed. The record became `src/SqlAggregateExpression.cs`, a public sealed class with the three get-only properties and an internal constructor; `Sql.Sum` (now `Sql.cs:21`) returns it (§5.2), and its consumers read the same three properties. | P4 |
| 82 | `ISqlDatabase` | Sql `:6` | model | sealed | `public sealed class SqlDatabase : DatabaseInstance`, which passes `supportsSchemaProvisioning: true` to the base constructor and overrides `ApplySchemaCoreAsync`. Until P6 it also lists `IDatabaseSchemaProvisioner` (row 8). **At P4 (re-verified, then landed):** promoted from `Internal/SqlDatabaseInstance.cs` (internal sealed, implementing `ISqlDatabase : IDatabase, IDatabaseSchemaProvisioner`) to `src/SqlDatabase.cs` in the `RootNamespace`, with an internal constructor that takes the typed engine; the base owns the name, the engine (re-exposed typed with `new`) and the disposed flag, and the model's `Dispose`/`DisposeAsync` became `DisposeCore`/`DisposeAsyncCore`. `CreateSessionAsync` is a typed `new` member over the base's NVI member (the offline refusal in the core), and `IsClosed` (internal) reads the disposed flag for the closed-database fix (§6.4). The session and transaction became public sealed leaves (`SqlDatabaseSession : DatabaseSession`, `SqlDatabaseTransaction : DatabaseTransaction`, rows 12 and 13), the server derives from `DatabaseServer` (row 9), and the server session is an internal sealed leaf (row 11). | P4 |
| 83 | `ISqlDatabaseEngineBuilder` | Sql `:10` | model | sealed | `public sealed class SqlDatabaseEngineBuilder`, with an internal constructor and a typed `AddServer(Func<SqlDatabaseEngine, DatabaseServer>)` (D5). `ExternalEngineBuilder` (`tests/SqlExpressionDepthExecutionTests.cs:1230`) is deleted, and its `ExpressionNestingLimit` cases (32 to 4096, checked in `Build()`) are retested against the sealed builder. **At P4 (re-verified, then landed):** `ExternalEngineBuilder` was at `:1230`, as listed, and is deleted with the interface. The builder left `Internal/` with the typed factories of row 6 and the deviation marker (§8); its `StorageStrategy` became internal with the option's (row 84). The retests in `SqlExpressionDepthExecutionTests`: `Create_LimitOutOfRange_ShouldThrow` drives the sealed builder too (its `Build()` refuses `int.MinValue`, 0, 31, 4097 and `int.MaxValue` with the engine's `ArgumentOutOfRangeException`, and a failed build freezes the builder), `Build_LimitAtTheRangeEnds_ShouldBuildAnEngineThatParsesWithIt` (32 and 4096) and `Builder_Limit_ShouldDefaultToTheEnginesAndReachTheEngineThroughBuild`. | P4 |
| 84 | `ISqlStorageStrategy` | Sql `:13` | model | abstract *(internal)* | `internal abstract class SqlStorageStrategy`, with an internal option property. The crash doubles derive through the existing grant. `CrashCaptureSqlStorageStrategy` is `public sealed` today (`tests/TestObjects/CrashCaptureSqlStorageStrategy.cs:20`) and becomes `internal sealed`, because a public class cannot derive from an internal one (CS0060); only Sql.Tests uses it. **At P4 (re-verified, then landed):** `Internal/SqlStorageStrategy.cs`, with `public abstract` members like the other models' strategies; `FileSystemSqlStorageStrategy` and `InMemorySqlStorageStrategy` derive from it, and the option's and the builder's `StorageStrategy` are internal. `CrashCaptureSqlStorageStrategy` is `internal sealed`, and the test strategies derive through the existing Sql → Sql.Tests grant; no grant was added. One consumer the row missed: Hosting.Tests compiled linked copies of `FaultInjectingJournalSqlStorageStrategy` and two Storage test helpers to fault a real SQL engine, and a linked copy cannot derive from the internal base, so those Hosting tests were rewritten on Hosting's engine doubles (§6.9). | P4 |
| 85 | `ISqlCatalog` | Sql.Catalog `:23` | model child | sealed | `public sealed class SqlCatalog`. It absorbs the static class (`SqlCatalog.cs:25`, `Open` at `:33` and `:53`), and `CaptureSnapshot` (`:72`) becomes an instance method. **At P4 (re-verified, then landed):** the static class had three more public statics over the interface that the row did not name, `ReserveTableAsync`, `PublishTableAsync` and `DropConstraintAsync` (`:106`, `:146`, `:169`), kept off the interface so a second implementation would not owe them; with one type they are instance members too, with the statics' documentation less their `InvalidCastException`. The internal `DefaultSqlCatalog` was folded in as `SqlCatalog.cs`, with the interface's documentation on its public members; the constructor is private behind the two `Open` overloads, and the static class's internal test helpers (`CreateSchemaTableAsync`, `CreateTableAsync` with constraints, `AddConstraintAsync`) are the instance's internal members, which the catalog's tests reach through the existing Sql.Catalog → Sql.Catalog.Tests grant. The project's `Abstractions/` folder is gone. No behavior changed: every member kept its checks. | P4 |
| 86 | `ISqlCatalogSnapshot` | Sql.Catalog `:14` | model child | sealed | `public sealed class SqlCatalogSnapshot`, with an internal constructor. **At P4:** landed, promoted from `Internal/SqlCatalogSnapshot.cs` to the `RootNamespace` with the interface's documentation. | P4 |
| 87 | `ISqlSchemaBuilder` | Sql.Schema `:9` | model child | sealed | `public sealed class SqlSchemaBuilder`, with an internal constructor. `Table<T>` is a non-virtual generic method. The SDK constant (`CSharpSchemaExtractor.cs:20`) changes in the same commit. **At P4 (landed):** promoted from the internal implementation to `src/SqlSchemaBuilder.cs` in the `RootNamespace`, with the interface's documentation; `Table<T>` and the other generic members are non-virtual, and the former implementation's public `AllowsDestructiveChanges` getter, which the interface did not have, is a private field. The extractor's constant (`CSharpSchemaExtractor.cs:20`) changed in the same commit. | P4 |
| 88 | `ISqlTableBuilder<TRow>` | Sql.Schema `:8` | model child | sealed | `public sealed class SqlTableBuilder<TRow>`. The SDK constants (`CSharpSchemaExtractor.cs:21`, `:729-730`) change in lockstep. **At P4 (landed):** `src/SqlTableBuilder.cs`; the constant is `SqlTableBuilder<TRow>` and the metadata name ``SqlTableBuilder`1`` (`:21`, `:729`). `PrimaryKey` and `References<TTarget>` were explicit interface implementations (the class had a private `PrimaryKey` property); they are public members over a private field. | P4 |
| 89 | `ISqlTypeBuilder` | Sql.Schema `:6` | model child | sealed | `public sealed class SqlTypeBuilder`, with the SDK constant at `:22`. **At P4 (landed):** `src/SqlTypeBuilder.cs`. | P4 |
| 90 | `ISqlPrincipalBuilder` | Sql.Schema `:6` | model child | sealed | `public sealed class SqlPrincipalBuilder`, with the SDK constant at `:23`. **At P4 (landed):** `src/SqlPrincipalBuilder.cs`. | P4 |
| 91 | `ISqlTriggerContext` | Sql.Schema `:6` | model child | sealed | `public sealed class SqlTriggerContext`, with a private constructor. It is a phantom that appears only inside trigger expression trees. The canonicalizer string (`CSharpExpressionCanonicalizer.cs:480`) changes in lockstep. **At P4 (landed):** `src/SqlTriggerContext.cs`; its `Audit<TValue>` throws `NotSupportedException`, never reached, because no instance exists. The runtime canonicalizer's allow-list (`SqlSchemaCompiler.cs`) and the SDK's (`CSharpExpressionCanonicalizer.cs:480`) moved together. The type identity is in a trigger's canonical text, so the hash of a schema that declares a trigger changed (§6.7). | P4 |
| 92 | `ISqlSchema` | Sql.Schema `:9` | model child | sealed | An opaque `public sealed class SqlSchema` that exposes `Name` and `Compile()` (§6.7). **At P4 (landed):** as planned (§6.7). | P4 |
| 93 | `ISqlSchemaTable` | Sql.Schema `:7` | model child | delete | The internal record (`Internal/SqlSchemaBuilder.cs:228`) stays internal behind `SqlSchema`. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 94 | `ISqlSchemaType` | Sql.Schema `:6` | model child | delete | The internal record (`:148`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 95 | `ISqlSchemaColumn` | Sql.Schema `:6` | model child | delete | The internal record (`:239`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 96 | `ISqlSchemaExtension` | Sql.Schema `:4` | model child | delete | The internal record (`:241`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 97 | `ISqlSchemaFunction` | Sql.Schema `:6` | model child | delete | The internal record (`:243`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 98 | `ISqlSchemaGrant` | Sql.Schema `:6` | model child | delete | The internal record (`:289`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 99 | `ISqlSchemaPrincipal` | Sql.Schema `:6` | model child | delete | The internal record (`:285`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 100 | `ISqlSchemaReference` | Sql.Schema `:6` | model child | delete | The internal record (`:237`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 101 | `ISqlSchemaTrigger` | Sql.Schema `:7` | model child | delete | The internal record (`:245`) stays internal. **At P4:** landed; the records live in `Internal/SqlSchemaDeclaration.cs` (the renamed `Internal/SqlSchemaBuilder.cs`), beside `SqlSchemaDeclaration`, the former `SqlSchemaModel`. | P4 |
| 102 | `ISqlClient` | Sql.Client `:19` | client | sealed | `public sealed class SqlClient`. It absorbs the static class (`SqlClient.cs:11`). | P5 |
| 103 | `ISqlClientObserver` | Sql.Client `:16` | client | abstract | `public abstract class SqlClientObserver`, with a protected constructor and `protected internal virtual` hooks with empty bodies. | P5 |
| 104 | `ISqlConnection` | Sql.Client `:18` | client | sealed | `public sealed class SqlConnection`, with an internal constructor. | P5 |
| 105 | `IDatabaseResourceDescriptor` | ApplicationModel `:6` | applicationmodel | keep | Unchanged. It extends the library-owned `IResourceCommandDescriptor`, following the 17-area pattern. | — |
| 106 | `IDatabaseApplicationTestFactory` | Testing `:20` | other | keep | Unchanged. It matches Web.Testing's `IWebApplicationTestFactory`. | — |

**Tally after corrections: delete 28, sealed 52, abstract 21 (16 public, 5 internal), keep 5.**
#1255 recorded the design's tally of 24, 56, 21 and 5. Three corrections move rows.
`ITransactionManager` moves from delete to sealed (C1). The nine Sql.Schema declaration
interfaces move from sealed to delete, because their records stay internal (C7). The four
storage sub-component interfaces (rows 31, 34, 37 and 38) move from delete to sealed, because
public `Storage` members return them (the member-access check above).

### 5.2 Public static factories and other public signatures

[Certain] These public members name a deleted interface, or collide with a type name the program
introduces. `rg` over `resources/Database --glob '**/src/**'` found them, and the phase-0 review
added the composition verbs, extension containers and factory below. Each has an explicit
decision.

| Type or member | Today | Decision | Phase |
|---|---|---|---|
| `LockManager` (`Transactions/src/LockManager.cs:8`, `Create` at `:16`) | `public static class`, returns `ILockManager` | The name passes to the sealed `LockManager`, and `public static LockManager Create()` moves onto it, so call sites are unchanged. **At P2:** landed as planned. | P2 |
| `TransactionManager` (`TransactionManager.cs:12`, `Create` at `:40`) | `public static class`, `Create(ITransactionLog, ILockManager, IVersionStore, Func<TransactionSequence>?)` | The name passes to the public sealed `TransactionManager`. A public `Create(LockManager, VersionStore, Func<TransactionSequence>? = null)` builds a standalone, non-durable manager over an in-memory log. The durable manager is the one `TransactionCoordinator` builds. An internal overload takes a `TransactionLog`, for the coordinator and Transactions.Tests. In P1 the parameters are still the interfaces; P2 retypes them. **At P2:** retyped to `LockManager` and `VersionStore`, both overloads. | P1/P2 |
| `TransactionLog` (`TransactionLog.cs:12`, `CreateInMemory` at `:19`, `CreateJournalBound(IStorageJournal)` at `:28`) | `public static class` | The public class is deleted. The name passes to `internal abstract class TransactionLog`, and its factories become internal statics on it. [Certain] Only tests called them: Transactions.Tests, and Indexing.Tests through the harness, which the new `Create` no longer needs. | P1 |
| `VersionStore` (`VersionStore.cs:8`, `CreateInMemory` at `:16`) | `public static class`, returns `IVersionStore` | The name passes to `public abstract class VersionStore` (row 45), and `CreateInMemory()` stays as a static on the base. **At P2:** landed as planned. | P2 |
| `SqlSchemaCompiler` (`Sql.Schema/src/SqlSchemaCompiler.cs:13`, `Compile(ISqlSchema, EngineModel)` at `:20`) | `public static class` | Becomes `internal static`. The public compile entry points are `SqlSchema.Compile(name, configure)` (static, unchanged) and the instance `SqlSchema.Compile()`. [Certain] Outside Sql.Schema, only Sql.Schema's own tests call it. **At P4 (landed):** `internal static`, with `Compile(SqlSchema, EngineModel)` reading the declaration behind the opaque type; the tests reach it through the new grant (§6.9), among them the four model-mismatch cases, which no public path can reach any more (the public entry points compile for `EngineModel.Sql` only). The review moved it to `src/Internal/SqlSchemaCompiler.cs` under `Assimalign.Cohesion.Database.Sql.Schema.Internal`, as `general-rules.md`'s folder table requires of an internal type (it had stayed in the `src/` root under the public namespace). | P4 |
| `ProtocolFraming` (`Protocol/src/ProtocolFraming.cs:11`, `CreateReader` at `:19`, `CreateWriter` at `:31`) | `public static class`, returns the frame interfaces | Deleted. The factories move onto the abstract bases (`ProtocolFrameReader.Create`, `ProtocolFrameWriter.Create`), the `Aes.Create()` shape. The callers are the four model servers, `ProtocolChannel`, two test clients and the Protocol tests. **Landed at P2:** the caller list held (the servers' at-capacity rejection writer, the Sql and KeyValuePair test protocol clients, `ProtocolFramingTests` and `ProtocolFamilyTests`). | P2 |
| `TransactionRecovery` (`TransactionRecovery.cs:18`, `Analyze` at `:25` and `:40`) | `public static class` taking `IStorageJournal` | Kept as a public static class: it is a stateless analysis with no interface twin. The parameter is retyped to `StorageJournal`. | P1 |
| `Sql.Sum<TSource>` (`Sql/src/Sql.cs:23`) | returns `ISqlAggregateExpression` | Returns `SqlAggregateExpression` (row 81). **At P4 (landed):** as planned. | P4 |
| `<Model>DatabaseEngine.CreateBuilder()` (`SqlDatabaseEngine.cs:181`, `KeyValueDatabaseEngine.cs:152`, Graph, Documents and Blob at `:67`) | return builder interfaces | Return the sealed builders. **KeyValuePair at P4:** returns `KeyValueDatabaseEngineBuilder`. **Graph at P4:** returns `GraphDatabaseEngineBuilder`. **Documents at P4:** returns `DocumentDatabaseEngineBuilder`. **Blob at P4:** returns `BlobDatabaseEngineBuilder` (Blob's `CreateBuilder` was at `BlobDatabaseEngine.cs:134`, not `:67`). **Sql at P4:** returns `SqlDatabaseEngineBuilder` (Sql's `CreateBuilder` was at `SqlDatabaseEngine.cs:254`, not `:181`). | P4 |
| The five composition verbs `AddSql`, `AddKeyValue`, `AddGraph`, `AddDocuments` and `AddBlob` (`Extensions/SqlDatabaseApplicationExtensions.cs:16`, `KeyValueDatabaseApplicationExtensions.cs:16`, `GraphDatabaseApplicationExtensions.cs:17`, `DocumentDatabaseApplicationExtensions.cs:17`, `BlobDatabaseApplicationExtensions.cs:17`) | take `Action<IDatabaseApplicationContext, I<Model>DatabaseEngineBuilder>` | Retyped to the sealed builder in each model's PR. They stay `extension(IDatabaseApplicationBuilder)` members on the kept seam. The templates call them with untyped lambdas (`cohesion-database/Program.cs:11`), so they compile unchanged. **KeyValuePair at P4:** `AddKeyValue(Action<IDatabaseApplicationContext, KeyValueDatabaseEngineBuilder>)`; a server factory inside it needs no cast. **Graph at P4:** `AddGraph(Action<IDatabaseApplicationContext, GraphDatabaseEngineBuilder>)`. **Documents at P4:** `AddDocuments(Action<IDatabaseApplicationContext, DocumentDatabaseEngineBuilder>)`; Hosting's `DatabaseCompositionTests` call it with an untyped lambda and compile unchanged. **Blob at P4:** `AddBlob(Action<IDatabaseApplicationContext, BlobDatabaseEngineBuilder>)`; no Hosting, Embedded or Studio code calls it, and the Blob builder tests call it with untyped lambdas. **Sql at P4:** `AddSql(Action<IDatabaseApplicationContext, SqlDatabaseEngineBuilder>)`. The templates, the SampleHost fixture and Hosting's tests call it with untyped lambdas and compile unchanged; the `(SqlDatabaseEngine)engine` casts inside their `AddServer` factories are identity casts now (§7). | P4 |
| `SqlDatabaseEngineFactory` (`Sql/src/SqlDatabaseEngineFactory.cs:8`) | `public static class` that forwards to `SqlDatabaseEngine.Create(options)` (`SqlDatabaseEngine.cs:163`) | Deleted in the Sql PR: rule 1 puts the factory on the type itself, which already has it. No code calls it. Its `(rootPath, engineName)` overload is not carried over, and the two doc mentions (`Database.Sql/docs/DESIGN.md:634`, `docs/programs/DATABASE_HOSTING_DESIGN.md:61`) change with it. **At P4 (landed):** deleted as planned, with its `(rootPath, engineName)` overload; no code called it. The Sql `DESIGN.md` mention went with the nesting-limit paragraph's rewrite, and the hosting design (now `:62`) says the factory was deleted. | P4 |
| `BlobContainerExtensions.GetOwnershipAsync` (`Blob/src/Extensions/BlobContainerExtensions.cs:13`) | `extension(IBlobContainer)` that casts to the internal implementation and throws for anything else | Folded into the sealed `BlobContainer` as an instance method, and the extension container is deleted. It existed only to avoid widening the interface, and a type in the same assembly needs no extension of itself. **At P4 (landed):** `BlobContainer.GetOwnershipAsync(CancellationToken)` is an instance member with the extension's body, its token check first included; `Extensions/BlobContainerExtensions.cs` is deleted. The extension's two refusals cannot be reached any more, so they went with it: a null container (`ArgumentNullException`) and a foreign implementation of the interface ("This blob container does not support ownership discovery."). `BlobOwnershipIntrospectionTests` and Studio's `BlobWorkspace` call the member unchanged. | P4 |
| `DatabaseClientStreamingExtensions.ExecuteStreamingAsync` (`Client/src/Extensions/DatabaseClientStreamingExtensions.cs:13`, parameter at `:30`) | `extension(IDatabaseClient)`, taking `IDatabaseStreamingExchange` | Folded into the sealed `DatabaseClient` as an instance method taking `DatabaseStreamingExchange`, for the same reason. The extension container is deleted. | P5 |
| `SqlProtocolConnectionExtensions.ExecuteAsync` (`Sql.Client/src/Extensions/SqlProtocolConnectionExtensions.cs:24`) | old-style `this IDatabaseConnection` extension | Stays an extension, because `DatabaseConnection` lives in Database.Client. It is retyped to `DatabaseConnection` and moves into an `extension(DatabaseConnection connection)` block (`general-rules.md`, extension containers). | P5 |
| `SqlCatalog.CaptureSnapshot` (`:72`), `KeyValueCatalog.CaptureSnapshot` (`:41`) | static, taking the catalog interface | Instance methods on the sealed catalogs. **KeyValuePair at P4:** landed (row 76). **Sql at P4:** landed with the sealed catalog (row 85); its one caller, the SQL executor's `CaptureCatalogSnapshot`, calls the instance member. | P4 |
| `BTreeIndexManager.EnsureFormat` (`Indexing/src/BTreeIndexManager.cs:55`) | takes `IStorage` | Takes `Storage`. | P1 |
| `BTreeIndexManagerOptions.Storage` (`Indexing/src/BTreeIndexManagerOptions.cs:16`) | `IStorage` (found at P1; the review missed it) | `Storage`. | P1 |
| The five model storages' `WriteAheadJournal` (`SqlStorage`, `KeyValueStorage`, `GraphStorage`, `DocumentStorage`, `BlobStorage`) | return `IStorageJournal` (found at P1) | Return `StorageJournal`. Their engines pass it to the coordinator, so no call site changes. | P1 |
| `TransactionCoordinator(IStorage, IStorageJournal, ITransactionRecordSpace)` and `TransactionCoordinator.Manager` | take the storage interfaces, expose `ITransactionManager` | Take `Storage` and `StorageJournal`, expose the sealed `TransactionManager`. `IsStorageOffline` and `AnalyzeAndScrub` lose their `is Storage` type tests, and `TransactionRecovery` its `ReadAll` fallback for non-`StorageJournal` journals. | P1 |
| `EmbeddedDatabase.Engines`, `TryGetEngine(string, …)` and `TryGetEngine(EngineModel, …)` (`Embedded/src/EmbeddedDatabase.cs:32`, `:75`, `:95`; the field at `:21-24`) | typed `IDatabaseEngine` | Typed `DatabaseEngine`. | P6 |
| The 15 triplet factories: `DatabaseClient`, `DatabaseCommandClient`, `BlobCatalog`, `BlobClient`, `DocumentCatalog`, `GraphCatalog`, `GraphClient`, `GraphSchema`, `GraphStore`, `KeyValueCatalog`, `KeyValueClient`, `SqlCatalog`, `SqlClient`, `BTreeIndexManager`, `DatabaseAuthenticator` | `public static class` + interface + internal implementation | Each collapses into one sealed type of the same name. `DatabaseAuthenticator` becomes abstract, with `AllowAll`. | per row |

### 5.3 Existing public abstract types, audited in the phase that touches them

| Type | Finding | Phase |
|---|---|---|
| `Storage.Model` (`Storage/src/Storage.cs:101`) | `public abstract StorageModel Model`. The value is fixed per storage, so it becomes a base field set by the protected constructor (rule 6). **At P2:** `protected Storage(StorageModel model, StorageStream data, ...)`. The five model storages and 21 test doubles (Storage.Tests 15, Transactions.Tests 5, Indexing.Tests 1; counted at P2) pass their model instead of overriding the getter. | P2 |
| `StorageJournal` (`Storage/src/Journal/StorageJournal.cs:21`) | After P1 it is the type the coordinator and recovery name. It has one shipped leaf, the sealed `StreamJournal` in the same assembly (`StreamJournal.cs:16`), so as it stands it meets none of rule 2's cases, and its constructor is `protected` (`:32`). P2's Storage commit, after #1236 settles the journal format, either collapses `StreamJournal` into one public sealed `StorageJournal`, keeping the file factories (`StreamJournal.cs:75`, `:86`), or records the second variant that justifies the base and narrows the constructor to `private protected` (rule 3). **Decided at P2: collapsed.** No second variant exists: no test or other assembly derives from `StorageJournal`, and #1236 settled the format with one medium. `StreamJournal` is deleted; its medium operations became private members of `public sealed class StorageJournal`, and its three public constructors (over a `Stream`, a `StorageStream`, an `IFileSystemFileHandle`) and two `FromFile` factories became the journal's own. The constructors stay public rather than internal behind new factories: they were already public API on the former leaf, and Transactions.Tests, which no grant reaches, builds journals over memory streams for `TransactionRecovery`. The deviation marker goes with the base (§8). **Landed per the 2026-10-06 decision (27):** the three public constructors became `public static StorageJournal Create(...)` overloads over a `Stream`, a `StorageStream` and an `IFileSystemFileHandle`, behind one private constructor; each refuses a null medium (and the stream overloads a stream that cannot read, write and seek) before it builds anything. `FromFile` keeps its name, as a named factory that says what it opens, like `StorageStream.FromFile`. Transactions.Tests reaches the public factories, so it needs no grant; `Storage` and every test caller (Storage.Tests, Transactions.Tests) call `Create`, and `JournalTests` pins the factory surface. | P2 |
| `CompiledSchema` (`Database/src/Provisioning/CompiledSchema.cs:12`) | A public abstract class whose leaf (`SqlCompiledSchema`) lives in Sql.Schema. It keeps a protected constructor. Audit its members for NVI when `DatabaseInstance.ApplySchemaAsync` starts taking it. **Audited at P3: conforms, unchanged.** `Format`, `Name`, `Model` and `AllowsDestructiveChanges` are set by the protected constructor and non-virtual (rule 6); `CanonicalDocument` is its one abstract public member, state the leaf computes (rule 4); `Hash` is non-virtual over it; nothing is virtual otherwise, and its one leaf lives in another shipped assembly, so the constructor stays `protected` (rule 3). It keeps no interface twin and is not newly tightened, so it carries no marker (§8). | P3 |
| `DatabaseEngineWorker` (`Database/src/DatabaseEngineWorker.cs:64`) | Public abstract `Name`, `Kind` and `Interval`, and a virtual `WaitForTrigger`. `Run`, `RunIteration` and the per-database reporting members are non-virtual already (#1268 and its review), over `protected abstract void RunIterationCore`; P3 finishes the NVI conversion (row 7). #1264's body lists it, but P3 is where the root bases are written, so it moves there. **At P3:** converted (row 7) and marked (§8). | P3 |
| `QueryRow` (`Execution/src/QueryRow.cs:8`) | Nine public abstract getters (`:13-69`). `GetValue` returns `object?` and boxes. It has four leaves in four assemblies (`SqlMaterializedRow`, `KeyValueMaterializedRow`, `GraphQueryRow`, `DocumentQueryRow`), so it is megamorphic in a multi-model host. It gets NVI with the ordinal checks in the base, and typed accessors on the hot path. | P8 |
| `QueryResultSet` (`QueryResultSet.cs:15`) | `GetRowsAsync` returns `IAsyncEnumerable<QueryRow>` (`:27`), which costs one interface dispatch per row. [Guessing] Replace it with a non-virtual pull shape only if a per-row benchmark shows the dispatch matters. | P8 |
| `QueryResult` (`QueryResult.cs:10`) | `Diagnostics` is `IReadOnlyList<Diagnostic>?` (`:33`). It becomes non-null (§6.8). | P8 |
| `QueryRequest` (`QueryRequest.cs:10`, `:36`) | Audit for NVI and constructor visibility. `Parameters` (`:29`, overridden in four model requests) stays nullable: `null` means the request carries none, which rule 9 leaves alone. | P8 |
| `QueryParser` and `QueryAnalyzer` (`Language/src/QueryParser.cs:12`, `QueryAnalyzer.cs:12`) | Audit for NVI. `QueryAnalyzer` is declared in a block-scoped namespace (`QueryAnalyzer.cs:7`, a forbidden pattern) with empty `<summary>` docs. `QueryParserOptions.Analyzers` exposes a mutable `List<QueryAnalyzer>` (`QueryParserOptions.cs:12`). | P8 |

## 6. Design notes for the corrected rows

### 6.1 The statement-snapshot view on `TransactionContext` (row 41)

Documents, Graph and Blob each wrap the transaction context in an identical decorator that pins a
read-committed statement's snapshot: `DocumentStatementContext`, `GraphStatementContext` and
`BlobStatementContext` (`Internal/DocumentOperation.cs:134`, `Internal/GraphOperation.cs:134`,
`Internal/BlobOperation.cs:136`). Each is constructed at `:38` as
`new XStatementContext(_context, _context.Snapshot)`. The callers live in three shipped assemblies,
so the view must be public: `public TransactionContext PinStatementSnapshot()`. The view shares
`Id`, `Sequence`, `IsolationLevel` and `State` with its parent and fixes `Snapshot` at the moment of
the call. Sharing `Sequence` is safe because the coordinator pairs statement brackets by sequence
(`_statementBrackets.TryGetValue(context.Sequence.Value, …)`, `TransactionCoordinator.cs:177`).
Sql's `SqlStatementContext` and KeyValuePair's `KeyValueStatementContext` are statement scopes
over the coordinator, not context decorators, and they are unaffected.

**At P2:** landed as designed. The view is a second, private constructor of the sealed type: it
copies the identity, sequence and isolation level, reads `State` through the transaction's own
context, and keeps the snapshot it was given. The three decorators are deleted, and each
operation's `InitializeAsync` calls `_context.PinStatementSnapshot()`. A manager still refuses to
commit or roll back a view, as it refused a decorator, now through an internal
`IsStatementView` check instead of the type test on the internal context class. The P2 review
hardened the view: the end claim, the apply admission and the apply drain are forwarded to the
transaction's own context, so the end flag and apply count exist once and the shared lock guards
one set of state. Transactions.Tests' `TransactionStatementViewTests` pins the contract (pinned
snapshot, shared identity and state, refused commit and rollback, forwarded admission and claim).

### 6.2 `LockManager` and the coordinator's engine view (row 39)

`TransactionCoordinator` builds one lock manager (`TransactionCoordinator.cs:87`). It hands that
instance to the transaction manager, and hands engine code a private `EngineLockManager` view
(`:624`). The view forwards acquisition. For a transaction the manager still tracks, it skips
`ReleaseAll`, which is what keeps a rolled-back writer whose undo is deferred holding its locks
(#1226, documented at `:109-125`). A sealed `LockManager` cannot be decorated, so the view becomes
an internal mode of the one type:

- an internal release filter installed by the coordinator;
- an internal, unfiltered release member that the transaction manager calls;
- the public `ReleaseAll`, which consults the filter.

A standalone `LockManager.Create()`, such as the Indexing harness uses, has no filter and behaves
as today. The #1226 tests in `TransactionCoordinatorRollbackTests.cs` gate this change.

**At P2:** the view had grown after this note was written. #1268's review gave it a second job:
`TransactionCoordinator.AbandonLockWaits` cancels, once the storage goes offline, every lock wait
in progress and every later one, through a cancellation source the view linked into each wait.
The internal mode therefore carries both: `EnterEngineMode(manager.IsTracked)` installs the
release filter and the abandon source together, `Abandon(cause)` fires the source, the public
`AcquireAsync` tries the grant at once and otherwise waits on the linked token, and the
transaction manager's two releases call the internal `ReleaseAllUnfiltered`. The manager's
`AbandonPending` call lost its type test, since every lock manager is the one type. A
standalone lock manager has no mode and runs the old `DefaultLockManager` code unchanged.

### 6.3 Resolving an index's storage transaction (row 23)

Four wrappers implement `IStorageTransactionSource` over `TransactionCoordinator.TryGetStorageTransaction`.
They are not identical:

| Engine | Wrapper | Throws when no bracket exists |
|---|---|---|
| Sql | `SqlDatabaseInstance.cs:412` | `DatabaseException` "Transaction {seq} has no statement bracket applying on this database." (`:433-434`) |
| KeyValuePair | `KeyValueDatabaseInstance.cs:462` | the same `DatabaseException` (`:483-484`) |
| Documents | `DefaultDocumentCatalog.Indexes.cs:310` | `InvalidOperationException` "Index mutation requires a shared statement bracket." (`:325`) |
| Graph | `DefaultGraphStore.Indexes.cs:223` | `InvalidOperationException` "Graph index mutation requires a shared statement bracket." (`:235`) |

The coordinator states the contract: the engine "supplies its own exception vocabulary when a
bracket is missing" (`TransactionCoordinator.cs:169`). `DatabaseException` lives in the area root,
which the Indexing child root may never reference. So `BTreeIndexManagerOptions` takes a
per-engine delegate, `Func<TransactionContext, StorageTransaction>`, and each engine passes a
lambda that keeps its own exception. The coordinator is **not** injected into Indexing. The four
wrapper classes go. The Indexing harness supplies its own delegate (`IndexTestHarness.cs:18`,
`BTreePageFormatTests.cs:53`).

**At P2:** landed as designed, re-verified first: the four wrappers and their messages were as
the table says. The property kept its name, `TransactionSource`. Each engine passes a method
group, a private `ResolveStatementBracket` over its readonly coordinator field with the wrapper's
body, rather than a lambda; the harness passes `GetStorageTransaction`, its pairing table's
lookup, and no longer implements anything.

### 6.4 Shared session and transaction behavior moves into the root bases (rows 12 and 13)

**The explicit-transaction state machine** (the #1225 follow-up) exists in four copies:
`GraphDatabaseTransaction.cs:19`, `DocumentDatabaseTransaction.cs:19`,
`BlobDatabaseTransaction.cs:19` and `KeyValueDatabaseTransaction.cs:32`. Each has its own
`SemaphoreSlim _endGate` (`:27`, `:27`, `:27`, `:41`). #1225's commit (`373e4408`) kept the copies
per model only because "the area root has no place for it without a new public type".
`DatabaseTransaction` is that type. It owns:

- the end gate that serializes commit, rollback, dispose and abort;
- a Faulted state for an end that did not complete;
- a rollback that succeeds and can be repeated;
- cancellation observed only before an end starts;
- a protected, non-virtual `Abort(Exception cause)`.

Each model supplies its vocabulary through protected abstract members. The codes are `COHDBG007`
(Graph), `COHDBD001` (Documents), `COHDBB001` (Blob) and `COHDBK001` (KeyValuePair).

Two contracts stay per model:

- **Which failures abort.** Graph, Documents and Blob abort the explicit transaction on a failed
  statement. Sql and KeyValuePair keep the statement-level contract, so a failed command leaves
  the transaction active. Those two never call `Abort`.
- **Sql's own transaction** (`SqlDatabaseTransaction.cs:18`) derives from the same base and gains
  its end gate.

**The "transaction already active" check** exists in five copies with three messages:

| Message | Sessions |
|---|---|
| "A transaction is already active on this session." | Sql (`SqlDatabaseSession.cs:122`), KeyValuePair (`KeyValueDatabaseSession.cs:81`) |
| "A transaction or operation is already active on this session." | Graph (`GraphDatabaseSession.cs:56-61`), Documents (`DocumentDatabaseSession.cs:56-61`) |
| "A transaction or stream is already active on this session." | Blob (`BlobDatabaseSession.cs:54-59`) |

`DatabaseSession` keeps one check with one message: "A transaction or operation is already active
on this session."

**When it takes effect.** No model session or transaction derives from the root bases until its
model PR in P4, so the consolidation lands in two steps:

- **P3** writes the bases with the consolidated state machine and the one check, and adds a
  Database.Tests suite that drives them through root-level test doubles (the bases' constructors
  are `protected`): the end gate, the Faulted state, a repeatable rollback, cancellation only
  before an end starts, `Abort`, and the one "already active" message. Model tests do not change
  in P3, because no model runs the base code yet.
- **Each model PR in P4** deletes that model's own copy (`GraphDatabaseTransaction.cs:19` and its
  siblings above), derives from the bases, adds the typed `new` fields, and updates that model's
  "already active" assertions.

**Gate:** P3 runs the new Database.Tests suite. Each model PR in P4 runs that model's #1188, #1225
and #1226 suites, in process and over the wire.

**At P3 (re-verified, then landed).** The four copies and the five checks were as listed. The
bases carry the most complete copy, Documents' and Blob's (they had the session teardown Graph
lacked, and keep a commit after it reporting the coded cause), with KeyValuePair's admission and
refusal wording folded in:

- `DatabaseTransaction` owns the end gate, `Faulted`, the repeatable rollback, cancellation only
  before an end starts (its cores take no token), and the offline refusal before a commit or
  rollback and the no-op teardown on an offline database (#1243). The plan's `Abort(Exception)` is
  `protected AbortAsync`, because the abort rolls back under the end gate; the teardown's
  `CloseAsync` (Documents, Blob and KeyValuePair carried it) joined it. A commit is refused while an
  operation of the transaction runs: KeyValuePair's `TryBeginCommand`/`EndCommand` and the other
  three models' `Operations` counter became one admission under the base's lock,
  `TryBeginOperation`/`EndOperation`. A model supplies `GetKernelState`, `CommitCoreAsync` and
  `RollbackCoreAsync` (with its own exception translation), `GetOfflineRefusal` and
  `CreateAbortedException` (its code). `IsOpen`, `IsUsable`, `CreateRefusal` and `CloseAsync` are
  `protected internal`, because the root's session base reads or calls them. `AbortAsync` is
  `protected` (P3 review): no root type calls it, and `protected internal` would not let a model
  session call it either (CS1540), so a model re-exposes it, like the other four, to its own
  session through its leaf.
- `DatabaseSession` owns the one check, plus the reservation of a BEGIN in flight and a protected
  operation hold (`TryEnterOperation`/`ExitOperation`) for the models whose sessions run one
  operation at a time (Graph, Documents and Blob's `_reserved` flag and operation set), and
  `ThrowIfTransactionRefuses` for the check every model made before a statement. Its teardown runs
  the leaf's `DisposeAsyncCore` (the models abort their running operations there), then ends the
  open transaction with the cause "The session closed before the transaction ended." Both steps
  run whatever the first threw, and any failure is reported in one `AggregateException`, "The
  session failed to close." (P3 review: the base first rethrew a single failure as itself, which
  disagreed with the engine base and with the three sessions that had a teardown aggregate).
- **Messages that change when a model adopts the bases** (P4 updates the assertions, R4): the
  "already active" check ("A transaction or operation is already active on this session." for
  all five); a closed session ("The session is closed.", for "The graph session is closed.",
  "Session is not open. Current state: Closed." and their siblings); a commit or rollback refused by
  state ("The transaction is {state}." and "The transaction is Committed; a committed transaction
  cannot roll back.", for KeyValuePair's and SQL's "Cannot commit transaction in state '…'"
  forms); a refused operation ("… start the operation after it ends." and "The session's
  transaction ended before the operation started; nothing was written.", for "statement",
  "command" and Blob's "operation"); a commit with work running ("An operation of the transaction
  is still running; commit after it completes.", for "Dispose every graph operation …", "Dispose
  every blob stream …" and KeyValuePair's command message); the teardown cause ("The session
  closed before the transaction ended.", for the three model-named causes Documents, Blob and
  KeyValuePair assert). Graph's commit after an abort and a second commit now both report
  `COHDBG007` with the cause, as Documents and Blob already did. Disposal failures (P3 review): a
  session reports any teardown failure in one `AggregateException` named "The session failed to
  close.", for "One or more {graph|document|blob} operations failed to close." (Graph, Documents
  and Blob change only the message; SQL and KeyValuePair, whose teardown let the transaction's
  failure out unwrapped, gain the wrapper); an engine names its disposal aggregate "One or more
  components of engine '{name}' failed to close.", for "One or more {graph|document|blob} engine
  components failed to close." and SQL's and KeyValuePair's "Engine disposal encountered
  failures." No model test asserts these messages; the engine-disposal tests assert only the
  `AggregateException` type, which stays.
- **Other behavior that changes when a model adopts the bases** (P3 review). [Likely] No current
  test asserts any of it (a search of the model suites found none), so each P4 PR adds the
  assertion where it matters:
  - *BEGIN's refusal order.* All five sessions refused an unsupported isolation level and an
    offline database before their "already active" check, and Graph, Documents and Blob refused
    the isolation level before a closed session too. The base checks the closed session, then
    "already active" (or the open transaction's refusal), then the token, and only then calls the
    leaf's core, where the isolation-level and offline refusals move. A BEGIN that fails two ways
    now reports the base's refusal.
  - *The token before the core* (KeyValuePair P4 review). `DatabaseInstance.CreateSessionAsync`
    and both `DatabaseSession.ExecuteAsync` seams check the token before the leaf's core. Where a
    model checked its offline database or its transaction's refusal before the token, a canceled
    token now gets `OperationCanceledException` instead of the model's code.
  - *Databases that fail to close* (KeyValuePair P4 review). The engine base adds whatever the
    leaf's `DisposeAsyncCore` throws to its aggregate as one component, so a leaf that aggregates
    several database failures reports them nested one level down, where a model's single
    aggregate held each failure directly.
  - *The engine's guards* (row 5). `GetDatabasesAsync` checks disposal and the token when it is
    called; every model's iterator checked disposal at its first `MoveNextAsync`. The other members
    check the name, then disposal, then the token; Sql and KeyValuePair checked disposal first and
    the name last, Graph, Documents and Blob the name, the token, then disposal. The base's
    constructor rejects a null, empty or white-space name, which every model accepts today
    (`options.EngineName ?? "<model>-engine"` passes `""` through), so each model PR rejects a
    blank `EngineName` in its options validation or lets the base's `ArgumentException` surface
    from `Create`.
  - *Blob's server* (row 9). A start refused while the engine is not `Running` becomes terminal;
    the Blob PR's start core disposes the listener before it rethrows (landed, pending owner
    confirmation; "Blob at P4" below).
  - *Engine composition* (P4.0, §6.5). When a model's builder moves from the bridge to its leaf's
    compose method, the attach checks are the base's (row 5). A custom worker whose name matches
    another worker of the engine, built-in or custom (ordinal, ignoring case), is refused with
    "Worker name '{name}' is already registered." and disposed, then the engine; Graph,
    Documents and Blob never checked names. SQL's and KeyValuePair's attach-time refusal of a blank
    name ("A worker must have a diagnostic name.", an `ArgumentException` after which the state
    disposed the worker) is gone: `DatabaseEngineWorker`'s constructor rejects a blank name inside
    the factory, so the build fails with the constructor's `ArgumentException` and has no product
    to dispose. The refusals of a product attached twice and of a server that fronts another engine
    keep their messages and their disposal: the foreign server is disposed, a repeated product is
    left to the engine. The pump threads are named for their workers, where Graph, Documents and
    Blob named them `{engine}/{kind}`; it is observable, because each of those models'
    worker-resilience suites asserts the thread on which its flush worker's failing journal write
    or fsync ran (`DocumentWorkerResilienceTests`, `BlobWorkerResilienceTests` and, until its P4
    PR, `GraphWorkerResilienceTests` expect `{engine}/WriteAheadFlush`), so each of those PRs
    moves that assertion to the worker's name. And SQL and KeyValuePair dispose every worker last
    attached first, instead of their checkpointer and then their custom workers (the root DESIGN
    listed this at P3; it was missing here until P4.0).
- **SQL in P4** gains the end gate, the repeatable rollback and a coded aborted error of its own
  (a commit of a transaction the kernel ended under its caller fails with that code instead of
  "Cannot commit transaction in state …"); it keeps the statement-level contract, so it never calls
  `AbortAsync`. Three more SQL changes follow from the same base (P3 review):
  `SqlDatabaseTransaction.CommitAsync` passes its token to the coordinator today, and under the
  base the token is observed only before the commit starts (`CommitCoreAsync` takes none); `State`
  reports `Faulted` for a transaction the kernel ended under its caller, where SQL returns the
  kernel state; and session disposal ends the open transaction through `CloseAsync` with the
  teardown cause, so a later commit reports SQL's coded error naming "The session closed before
  the transaction ended." instead of "Cannot commit transaction in state 'RolledBack'.".

**KeyValuePair at P4 (re-verified, then landed).** The model's copy was as listed
(`KeyValueDatabaseTransaction.cs` with its own `_endGate`, the session's "A transaction is already
active on this session." check). Both are deleted; the leaves supply `GetKernelState`,
`CommitCoreAsync`/`RollbackCoreAsync` with the kernel translation, `GetOfflineRefusal`
(`COHDBK002`) and `CreateAbortedException` (`COHDBK001`, unchanged wording). The session keeps the
statement-level contract of the 2026-10-04 decision: it never calls `AbortAsync`, commands do not
take the session's operation hold (an auto-commit command and a BEGIN may still run side by side),
and a command is admitted into the explicit transaction through the base's
`TryBeginOperation`/`EndOperation`, which the leaf re-exposes to its session as
`TryBeginCommand`/`EndCommand`/`CreateCommandRefusal` (the base members are `protected` or
`protected internal`, which a model session cannot reach). Each change of this section is
accounted for:

- *Asserted in the model's suites:*
  - *BEGIN's refusal order.* A Serializable BEGIN on an active session gets the "already active"
    message, and the Serializable refusal only when no transaction is open
    (`KeyValueTransactionFailureTests`). On an offline database, BEGIN from the session that holds
    an open transaction gets the "already active" message where it got `COHDBK002`
    (`KeyValueStorageOperationsTests`). BEGIN refuses a transaction the kernel ended under its
    caller with `COHDBK001` before it checks a canceled token, where it threw the token's
    `OperationCanceledException` (`KeyValueTransactionFailureTests`, the database dropped under
    the session).
  - *The token before the core.* `CreateSessionAsync` and both execute seams check the token
    before their cores, so a canceled token gets `OperationCanceledException` where an offline
    database (`COHDBK002`, `KeyValueStorageOperationsTests`) or a transaction that refuses
    commands (`COHDBK001`, `KeyValueTransactionFailureTests`) was reported first.
  - *Messages.* The closed-session message for BEGIN and both execute seams
    (`KeyValueTransactionFailureTests`); "The transaction is Committed; a committed transaction
    cannot roll back." and "The transaction is RolledBack." (same suite, the first replacing a
    `Contains("Committed")`); "An operation of the transaction is still running; commit after it
    completes." (`KeyValueLifecycleTests`, replacing a `Contains("still running")`); the teardown
    cause "The session closed before the transaction ended." (`KeyValueTransactionFailureTests`).
  - *The offline teardown's state.* A transaction whose session closed while its database was
    offline (the teardown rolls nothing back) reports `Faulted`, where the model reported
    `Active`; it refuses everything either way (`KeyValueStorageOperationsTests`, and the root
    suite's `DisposeCloseAbort_Offline_ShouldNotRollBack`).
  - *The engine.* The guard order, the enumeration's disposal check at its call and
    `TryGetDatabase`'s new name check (`KeyValueEngineLifecycleTests`). The disposal aggregate's
    shape when databases fail to close (same suite): the leaf's `DisposeAsyncCore` reports one
    failure as itself and several in one `AggregateException` ("One or more key-value databases
    failed to close."), which the base adds to its own aggregate as one component, where the
    model's single aggregate held each database's failure directly. A fully flat shape needs the
    base to accept several failures from `DisposeAsyncCore`, a root-base change left to the
    owner. The disposal aggregate's message, the blank `EngineName` refusal, the duplicate and
    blank worker names and the composition paths of §6.5 (`KeyValueEngineCompositionTests`).
- *Not reachable from the model without a kernel hook, pinned by the root suite
  (`DatabaseTransactionTests`, `DatabaseSessionTests`):* the two refused-operation messages ("…
  start the operation after it ends." and "… ended before the operation started; nothing was
  written.") need the transaction ended with its kernel transaction still active: transiently
  while a caller's commit or rollback runs, and persistently while the database closes and the
  kernel refuses an end before it starts; neither can be held open from the model without a
  kernel hook. The session's "The session failed to close." aggregate needs the session's
  teardown to fail, which needs the same race (an offline database's teardown touches nothing,
  and the kernel ends a started rollback whatever fails, #1226). The server session's cleanup now
  ignores that `AggregateException` where it ignored the `DatabaseException` before, so a
  connection's teardown still never faults.
- *Not observable through the public surface, so not asserted:* the worker disposal order (factory
  workers before the built-in ones; the checkpointer's lanes end before the storages close either
  way) and the pump threads' names (KeyValuePair already named them for their workers).
- *A database closed outside the engine* (found by the Blob review, §7 "Blob, as landed"; fixed
  for this model by the follow-up of 2026-10-06, §7 "The closed-database follow-up"). The engine
  keeps a database its holder disposed (directly; `session.Database` is the same instance)
  registered, and `IsOpen` read only that registration, so the version-purge worker failed on its
  disposed coordinator every pass and the engine stayed `Faulted` for good (24 failed passes in
  half a second at 20 ms intervals, reproduced at `e092cada`); the key-value server does not read
  the engine's state, so it kept serving, and `Database.Hosting` reported the engine degraded.
  `KeyValueDatabase.IsClosed`, `KeyValueDatabaseEngine.IsOpen` false for a closed database, and
  the version-purge worker (pass and trigger wait), the flush worker and the write-back worker,
  which visit databases here, skip a closed one; so does the checkpointer, through
  `KeyValueCheckpointWorker.IsCheckpointDue` (false for a closed database; the shared pass's
  `IsOpen` check covers only a checkpoint that raced the close). The fix's review found the
  checkpointer's path: a close that is not idle (a deferred undo still failing, whose writer the
  close keeps in flight, #1226) leaves the data journal untruncated, and when the checkpointer had a
  failure recorded for the database, every poll's refused checkpoint kept it recorded and the
  engine `Faulted`. The reopen is unchanged: `OpenDatabaseAsync` returns the closed instance, which
  refuses a session with `ObjectDisposedException` (the other engines refuse the reopen itself).
  Asserted:
  `KeyValueWorkerResilienceTests.DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunningAndItsServerServing`
  (20 ms worker intervals, half a second of passes over the closed database, then one more pass
  of each worker: every pass succeeds, no worker records a failure, the engine is `Running`, a
  server starts and serves a handshake and a `PUT` to another database, and the reopen returns
  the closed instance) and
  `KeyValueWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
  (a checkpoint failure recorded, then a close with a deferred undo's writer in flight: the
  failure ends and the engine runs again).

**Graph at P4 (re-verified, then landed).** The model's copy was as listed
(`GraphDatabaseTransaction.cs` with its own `_endGate`, its `Operations` counter and its
`CreateRefusal`, and the session's "already active" check, whose message was already the base's,
with its `_reserved` flag and operation set). All are deleted; the leaves supply
`GetKernelState`, `CommitCoreAsync`/`RollbackCoreAsync` with the model's translation (the offline
refusal first, then `TranslateKernelFailure`), `GetOfflineRefusal` (`COHDBG012`) and
`CreateAbortedException` (`COHDBG007`, unchanged wording). The session keeps the per-statement
contract of #1188: a failed statement aborts the transaction through the base's `AbortAsync`,
which the leaf re-exposes to its session as `AbortForFailedStatementAsync`. A statement holds the
session through the base's operation hold (`TryEnterOperation`/`ExitOperation`) from its start to
its end, which replaces `_reserved` and the operation set (one statement at a time, as before; a
second is still refused with the model's "Dispose the active graph operation before starting
another operation on this session."), and is admitted into the explicit transaction through
`TryBeginOperation`/`EndOperation`, re-exposed as `TryBeginStatement`/`EndStatement`/
`CreateStatementRefusal`, which replaces the `Operations` counter. The session's
`DisposeAsyncCore` aborts the running statement with the model's cause ("The graph session closed
while the operation was running."), then the base ends the transaction. Each change of this
section that reaches Graph is accounted for:

- *Asserted in the model's suites:*
  - *BEGIN's refusal order.* A Serializable BEGIN is refused "already active" on a session with an
    active transaction and "The session is closed." on a closed one, and gets the isolation-level
    refusal only when neither holds (`GraphTransactionFailureTests`). On an offline database, BEGIN
    from the session that holds an open transaction is refused "already active" where it got
    `COHDBG012` (`GraphStorageOperationsTests`). BEGIN on a session whose transaction the kernel
    ended under its caller (the database was dropped) is refused with `COHDBG007`, where the model
    reported the disposed database (`ObjectDisposedException`; `GraphTransactionFailureTests`);
    once the caller rolled that transaction back, BEGIN reports the disposed database again (same
    suite). Found by the PR's review: BEGIN and both execute seams refuse a closed session as
    closed before they check its database, so a closed session of a dropped database gets "The
    session is closed." where the model, which checked the database first, reported
    `ObjectDisposedException`; the typed operations, `GraphSchema.Open` and the schema operations
    check the database first and still report it (`GraphTransactionFailureTests`).
  - *The one-statement hold.* A statement holds its session through the base's operation hold
    from its start to its end: while one waits for the writer lock, a second statement through
    either seam, a typed operation and a schema read are refused with the model's "Dispose the
    active graph operation before starting another operation on this session.", BEGIN with the
    base's "already active" message, and the waiting statement still completes
    (`GraphTransactionFailureTests`). The hold replaced the model's `_reserved` flag; the messages
    are the ones the model reported.
  - *The token before the core.* `CreateSessionAsync`, both execute seams and BEGIN refuse a
    canceled token before `COHDBG012` (`GraphStorageOperationsTests`), and both execute seams
    refuse it before the disposed database of a dropped one (`GraphTransactionFailureTests`). The
    seams' argument checks, a null request and a blank statement, come before the offline refusal
    too, where the model checked the database first (`GraphStorageOperationsTests`). The typed node
    and relationship operations do not pass the session's seams and keep the model's order, the
    offline refusal first (asserted beside them).
  - *Messages.* The closed-session message for BEGIN, both execute seams, a typed operation, a
    schema operation and `GraphSchema.Open` (`GraphTransactionFailureTests`, for "The graph session
    is closed."); "An operation of the transaction is still running; commit after it completes."
    (same suite, a statement waiting for the writer lock, for "Dispose every graph operation before
    committing its transaction."), with the transaction left active and committable; the teardown
    cause "The session closed before the transaction ended."; "The transaction is Committed; a
    committed transaction cannot roll back." and "The transaction is RolledBack." pinned exactly
    (Graph already used both).
  - *Graph's commits after an abort.* A second commit of an aborted transaction, a commit after the
    session's teardown ended an aborted transaction, and a commit after the teardown aborted a
    running statement all report `COHDBG007` with the statement's cause, where the model reported
    "The transaction is RolledBack." (`GraphTransactionFailureTests`).
  - *The offline teardown's state.* `Faulted`, where the model reported `Active`
    (`GraphStorageOperationsTests`).
  - *The engine.* The guard order, with the model's own name rule (a single file-name component)
    now in the cores after the base's checks, so `..` on a disposed engine is
    `ObjectDisposedException` and with a canceled token `OperationCanceledException`, where both
    were `ArgumentException`; the enumeration's disposal check at its call; the typed
    `TryGetDatabase` overload (`GraphEngineTests`). The disposal aggregate's message and the nested
    database aggregate, "One or more graph databases failed to close." (`GraphEngineTests`,
    `GraphEngineCompositionTests`). The blank `EngineName` refusal, the duplicate worker names
    (built-in and custom, which the model never checked), a blank worker name failing inside its
    factory, the pump threads named for their workers (a custom worker's in
    `GraphEngineCompositionTests`, the built-in flush worker's in `GraphWorkerResilienceTests`,
    which expected `graph-engine/WriteAheadFlush`), and the composition paths of §6.5
    (`GraphEngineCompositionTests`).
  - *Typed parameters.* A null session or database given to a typed operation or `GraphSchema.Open`
    is an `ArgumentNullException`, where the interface-typed members reported `COHDBG005`; a
    session of another database is still `COHDBG005` (`GraphDatabaseScopeTests`).
- *Not reachable from the model without a kernel hook, pinned by the root suite* (as for
  KeyValuePair): the two refused-operation messages, "… start the operation after it ends." (for
  the model's "… start the statement after it ends.") and "… ended before the operation started;
  nothing was written." (where the model reported `COHDBG007` without a cause), and the session's
  "The session failed to close." aggregate (for "One or more graph operations failed to close.").
- *Unchanged for Graph:* the "already active" message (the model's was the base's), the worker
  disposal order (the model disposed its workers last attached first already) and the server
  lifecycle (the base carries the one the model's server had). The database's disposal steps and
  their order are unchanged as well, though their path moved: `GraphDatabase.DisposeAsync` awaits
  the coordinator's and the storage's `DisposeAsync` where the model's ran the synchronous
  `Dispose`, and the engine closes its databases after clearing them under its lock where it
  disposed them inside it (recorded with the Documents landing, which made the same move).
- *A database closed outside the engine* (found by the Blob review, §7 "Blob, as landed"; fixed
  for this model by the follow-up of 2026-10-06, §7 "The closed-database follow-up"). The engine
  keeps a database its holder disposed (directly; `session.Database` is the same instance)
  registered, to refuse its reopen, and `IsOpen` read only that registration, so the
  version-purge worker failed on its disposed coordinator every pass and the engine stayed
  `Faulted` for good (21 failed passes in half a second at 20 ms intervals, reproduced at
  `e092cada`); the Graph server does not read the engine's state, so it kept serving, and
  `Database.Hosting` reported the engine degraded. `GraphDatabase.IsClosed`,
  `GraphDatabaseEngine.IsOpen` false for a closed database and its storage, the version-purge
  worker skips a closed database in its pass and its trigger wait, and the checkpointer skips it
  through `GraphCheckpointWorker.IsCheckpointDue` (false for a closed database; the shared pass's
  `IsOpen` check covers only a checkpoint that raced the close). The fix's review found the
  checkpointer's path: a close that is not idle (a deferred undo still failing, whose writer the
  close keeps in flight, #1226) leaves the journal untruncated, and when the checkpointer had a
  failure recorded for the database, every poll's refused checkpoint kept it recorded and the
  engine `Faulted`. The flush and write-back workers visit storages, so they still visit the
  closed database's storage and tolerate its `ObjectDisposedException` through
  `IsOpen(GraphStorage)`, as Blob's do; write-back writes nothing for a disposed storage. The
  refused reopen is unchanged. Asserted:
  `GraphWorkerResilienceTests.DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunningAndItsServerServing`
  (20 ms worker intervals, half a second of passes over the closed database, then one more pass
  of each worker: every pass succeeds, no worker records a failure, the engine is `Running`, a
  server starts and serves a handshake and an insert to another database, and the closed one's
  reopen is still refused) and
  `GraphWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
  (a checkpoint failure recorded, then a close with a deferred undo's writer in flight: the
  failure ends and the engine runs again).

**Documents at P4 (re-verified, then landed).** The model's copy was as listed
(`DocumentDatabaseTransaction.cs` with its own `_endGate`, its `Operations` counter, `CloseAsync`
and `CreateRefusal`, and the session's "already active" check, whose message was already the
base's, with its `_reserved` flag and operation set; the code mirrored Graph's pre-P4 model line
for line, the session-bound database view apart). All are deleted; the leaves supply
`GetKernelState`, `CommitCoreAsync`/`RollbackCoreAsync` with the model's translation (the offline
refusal first, then `TranslateKernelFailure`), `GetOfflineRefusal` (`COHDBD002`) and
`CreateAbortedException` (`COHDBD001`, unchanged wording). The session keeps the per-statement
contract of #1225: a failed statement aborts the transaction through the base's `AbortAsync`,
re-exposed as `AbortForFailedStatementAsync`; a statement (an OQL statement, a collection
operation of the session, or a document operation run in the session) holds the session through
the base's operation hold from its start to its end, which replaces `_reserved` and the operation
set (a second one is still refused with the model's "Dispose the active document operation before
starting another operation on this session."), and is admitted into the explicit transaction
through `TryBeginOperation`/`EndOperation`, re-exposed as `TryBeginStatement`/`EndStatement`/
`CreateStatementRefusal`, which replaces the `Operations` counter. The database's own collection
operations run outside any session and take neither. The session's `DisposeAsyncCore` aborts the
running statement with the model's cause ("The document session closed while the operation was
running."), then the base ends the transaction. Each change of this section that reaches
Documents is accounted for:

- *Asserted in the model's suites:*
  - *BEGIN's refusal order.* A Serializable BEGIN is refused "already active" on a session with an
    active transaction and "The session is closed." on a closed one, and gets the isolation-level
    refusal only when neither holds; the model checked the isolation level first, before the
    closed session too (`DocumentTransactionFailureTests`). On an offline database, BEGIN from the
    session that holds an open transaction is refused "already active" where it got `COHDBD002`
    (`DocumentStorageOperationsTests`). BEGIN on a session whose transaction the kernel ended under
    its caller (the database was dropped) is refused with `COHDBD001`, where the model reported the
    disposed database (`ObjectDisposedException`); once the caller rolled that transaction back,
    BEGIN reports the disposed database again (`DocumentTransactionFailureTests`). BEGIN and both
    execute seams refuse a closed session as closed before they check its database, so a closed
    session of a dropped database gets "The session is closed." where the model reported
    `ObjectDisposedException`; the session's collection operations and a collection's document
    operations check the database first and still report it (same suite).
  - *The one-statement hold.* While a statement waits for the writer lock, a second statement
    through either seam, a collection operation of the session and a document operation are
    refused with the model's message, BEGIN with the base's "already active" message, and the
    waiting statement still completes (`DocumentTransactionFailureTests`). The messages are the
    ones the model reported; the hold is the base's.
  - *The token before the core.* `CreateSessionAsync`, both execute seams and BEGIN refuse a
    canceled token before `COHDBD002`, and both execute seams refuse it before the disposed
    database of a dropped one (`DocumentStorageOperationsTests`, `DocumentTransactionFailureTests`).
    The seams' argument checks, a null request and a blank statement, come before the offline
    refusal too, where the model checked the database first (`DocumentStorageOperationsTests`).
    The collection and document operations do not pass the session's seams and keep the model's
    order, the offline refusal first (asserted beside them).
  - *Messages.* The closed-session message for BEGIN, both execute seams, the session's four
    collection operations and a collection's document operations (`DocumentTransactionFailureTests`,
    for "The document session is closed."); "An operation of the transaction is still running;
    commit after it completes." (same suite, a statement waiting for the writer lock, for "Dispose
    every document operation before committing its transaction."), with the transaction left
    active and committable; the teardown cause "The session closed before the transaction ended."
    (for "The document session closed before the transaction ended."); "The transaction is
    Committed; a committed transaction cannot roll back." and "The transaction is RolledBack."
    pinned exactly (Documents already used both, the first behind a `Contains("Committed")`).
  - *The engine.* The guard order, with the model's own name rule (a single file-name component)
    now in the cores after the base's checks, so `..` on a disposed engine is
    `ObjectDisposedException` and with a canceled token `OperationCanceledException`, where both
    were `ArgumentException`; the enumeration's disposal check at its call; the typed
    `TryGetDatabase` overload (`DocumentEngineTests`). The disposal aggregate's message and the
    nested database aggregate, "One or more document databases failed to close."
    (`DocumentEngineTests`, `DocumentEngineCompositionTests`). The blank `EngineName` refusal, the
    duplicate worker names (built-in and custom, which the model never checked), a blank worker
    name failing inside its factory, the pump threads named for their workers (a custom worker's in
    `DocumentEngineCompositionTests`, the built-in flush worker's in
    `DocumentWorkerResilienceTests`, which expected `document-engine/WriteAheadFlush`), and the
    composition paths of §6.5 (`DocumentEngineCompositionTests`).
  - *Option B* (§6.6). `session.Database` is the unbound database: its collection operations run
    in autocommit whatever transaction the session holds, where they ran in it; it creates a
    session after the session closed, where the view refused with "The document session is
    closed."; and disposing it closes the database for every session, not the session, after
    which the engine refuses to reopen it with `ObjectDisposedException` until it is dropped or
    the engine is recreated, as a directly disposed database always was
    (`DocumentTransactionFailureTests`, the option-B test). Because those operations no longer
    join the session's transaction, a write through `session.Database` while that transaction has
    written waits for the transaction's writer lock (one writer at a time), so a caller that
    awaits it before ending the transaction waits until the call's token is canceled; the
    canceled wait writes nothing and leaves the transaction committable (same suite,
    `CreateCollectionAsync_ThroughSessionDatabaseAfterTransactionWrote_ShouldWaitForItsWriterLock`).
    Inside a transaction, the session's own collection operations are the ones to use.
  - *Typed parameters.* A collection's operations take `DocumentDatabaseSession`: a null one is
    still an `ArgumentNullException`, and a session of another database or another session than a
    bound collection's is still the model's `DatabaseException` (`DocumentDatabaseScopeTests`).
- *Not reachable from the model without a kernel hook, pinned by the root suite* (as for
  KeyValuePair and Graph): the two refused-operation messages, "… start the operation after it
  ends." (for the model's "… start the statement after it ends.") and "… ended before the
  operation started; nothing was written." (where the model reported `COHDBD001` without a cause),
  and the session's "The session failed to close." aggregate (for "One or more document operations
  failed to close.").
- *Unchanged for Documents, and asserted where the bases now carry it:* the "already active"
  message (the model's was the base's); every commit of an aborted transaction reporting
  `COHDBD001` with the cause, a second commit included, a commit after the teardown aborted a
  running statement reporting that statement's cause, and a commit after the session closed on a
  transaction a failed statement had already aborted reporting that statement's failure, which
  the base's teardown keeps rather than its own cause (the model already closed the transaction
  with a cause, unlike Graph; `DocumentTransactionFailureTests`, the last as
  `CommitAsync_AfterSessionClosed_ShouldReportWhyNothingCommitted(aborted: true)` beside the
  active case, as Graph pins both); the offline teardown's `Faulted` state (the model's
  `CloseAsync` recorded the cause too, so it already reported `Faulted`, unlike KeyValuePair and
  Graph; `DocumentStorageOperationsTests`); and the worker disposal order (the
  model disposed its workers last attached first already). Documents has no server, so the server
  lifecycle does not reach it. The database's disposal steps and their order are unchanged too
  (the coordinator, then the storage: shutdown flush, journal, pool, streams), but their path
  moved: `DocumentDatabase.DisposeAsync` now awaits the coordinator's and the storage's
  `DisposeAsync` where the model's ran the synchronous `Dispose`, and the engine closes its
  databases after it has cleared them under its lock where it disposed them inside it
  (`Storage.Dispose` and `DisposeAsync` run the same steps; Graph's landing made the same move).
- *A database closed outside the engine* (found by the Blob review, §7 "Blob, as landed"; fixed
  for this model by the follow-up of 2026-10-06, §7 "The closed-database follow-up"). The engine
  keeps a database its holder disposed registered, to refuse its reopen, and `IsOpen` read only
  that registration, so the version-purge worker failed on its disposed coordinator every pass and
  the engine stayed `Faulted` for good (21 failed passes in half a second at 20 ms intervals,
  reproduced at `e092cada` both ways); `Database.Hosting` reported the engine degraded. A directly
  disposed database did this before P4 too; option B made it reachable from the session's own
  property, where disposing the view only closed the session. `DocumentDatabase.IsClosed`,
  `DocumentDatabaseEngine.IsOpen` false for a closed database and its storage, the version-purge
  worker skips a closed database in its pass and its trigger wait, and the checkpointer skips it
  through `DocumentCheckpointWorker.IsCheckpointDue` (false for a closed database; the shared
  pass's `IsOpen` check covers only a checkpoint that raced the close). The fix's review found the
  checkpointer's path: a close that is not idle (a deferred undo still failing, whose writer the
  close keeps in flight, #1226) leaves the journal untruncated, and when the checkpointer had a
  failure recorded for the database, every poll's refused checkpoint kept it recorded and the
  engine `Faulted`. The flush and write-back workers visit storages, so they still visit the
  closed database's storage and tolerate its `ObjectDisposedException` through
  `IsOpen(DocumentStorage)`, as Blob's do; write-back writes nothing for a disposed storage. The
  refused reopen is unchanged, and the session's remarks (class and `Database`) now say the
  workers skip the closed database, as Blob's do. Asserted:
  `DocumentWorkerResilienceTests.DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunning`,
  a theory that closes the database directly and through `session.Database` (20 ms worker
  intervals, half a second of passes over the closed database, then one more pass of each worker:
  every pass succeeds, no worker records a failure, the engine is `Running`, the other database
  takes writes, and the reopen is still refused), and
  `DocumentWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
  (a checkpoint failure recorded, then a close with a deferred undo's writer in flight: the
  failure ends and the engine runs again). Documents has no server.

**Blob at P4 (re-verified, then landed).** The model's copy was as listed
(`BlobDatabaseTransaction.cs` with its own `_endGate`, its `Operations` counter, `CloseAsync`,
`AbortAsync` and `CreateRefusal`, and the session's "already active" check, "A transaction or
stream is already active on this session.", with its `_reserved` flag and operation set; the
code mirrored Documents' pre-P4 model, the vocabulary apart: Blob says "operation" where
Documents said "statement", and an operation of Blob is a container operation or a blob
operation of a container, a stream's lasting until the stream is disposed). All are deleted;
the leaves supply `GetKernelState`, `CommitCoreAsync`/`RollbackCoreAsync` with the model's
translation (the offline refusal first, then `TranslateKernelFailure`), `GetOfflineRefusal`
(`COHDBB002`) and `CreateAbortedException` (`COHDBB001`, unchanged wording). The session keeps the
per-operation contract of #1225: a failed operation aborts the transaction through the base's
`AbortAsync`, re-exposed as `AbortForFailedOperationAsync`; an operation (a container operation of
the session, or a blob operation of a container bound to it, an open stream included) holds the
session through the base's operation hold from its start to its end, which replaces `_reserved`
and the operation set (a second one is still refused with the model's "Dispose the active blob
stream before starting another operation on this session."), and is admitted into the explicit
transaction through `TryBeginOperation`/`EndOperation`, re-exposed as
`TryBeginBlobOperation`/`EndBlobOperation`/`CreateOperationRefusal`, which replaces the
`Operations` counter. The database's own container operations, and those of a container it
returned, run outside any session and take neither. The session's `DisposeAsyncCore` aborts the
running operation with the model's cause ("The blob session closed while the operation was
running."), then the base ends the transaction. The wire server's abort of a host transaction for
a failure that ends the connection (`AbortTransactionAsync`) stays a session member over
`AbortForFailedOperationAsync`, without the hold (the server's pump runs one exchange at a time,
and its operations have ended when it aborts). Each change of this section that reaches Blob is
accounted for:

- *Asserted in the model's suites:*
  - *BEGIN's refusal order.* A Serializable BEGIN is refused "already active" on a session with an
    active transaction and "The session is closed." on a closed one, and gets the isolation-level
    refusal only when neither holds; the model checked the isolation level first, before the
    closed session too (`BlobTransactionFailureTests`). On an offline database, BEGIN from the
    session that holds an open transaction is refused "already active" where it got `COHDBB002`
    (`BlobStorageOperationsTests`). BEGIN on a session whose transaction the kernel ended under its
    caller (the database was dropped) is refused with `COHDBB001`, where the model reported the
    disposed database (`ObjectDisposedException`); once the caller rolled that transaction back,
    BEGIN reports the disposed database again (`BlobTransactionFailureTests`). BEGIN and both
    execute seams refuse a closed session as closed before they check its database, so a closed
    session of a dropped database gets "The session is closed." where the model reported
    `ObjectDisposedException`; the session's container operations and a bound container's
    operations check the database first and still report it (same suite).
  - *The one-operation hold.* While an upload stream of a container bound to the session is open,
    a blob operation of that container and a container operation of the session are refused with
    the model's message, and BEGIN with the base's "already active" message, for the model's "A
    transaction or stream is already active on this session."; the upload still publishes once
    disposed, and the session then runs operations and BEGIN again (`BlobTransactionFailureTests`).
    Over the wire, an upload on a connection whose engine session holds a host transaction is
    refused at the server's own BEGIN with the base's message (Blob.Client's
    `BlobTransactionFailureWireTests`).
  - *The token before the core.* `CreateSessionAsync`, both execute seams and BEGIN refuse a
    canceled token before `COHDBB002` (`BlobStorageOperationsTests`), and both execute seams refuse
    it before the disposed database of a dropped one (`BlobTransactionFailureTests`). The seams'
    argument checks, a null request and a blank statement, come before the offline refusal too,
    where the model checked the database first (`BlobStorageOperationsTests`). The container and
    blob operations do not pass the session's seams and keep the model's order, the offline
    refusal first (asserted beside them).
  - *Messages.* The closed-session message for BEGIN, both execute seams, the session's four
    container operations and a bound container's six operations (`BlobTransactionFailureTests`,
    for "The blob session is closed."), and for a write to an open stream of a session that closed;
    "An operation of the transaction is still running; commit after it completes." (for "Dispose
    every blob stream before committing its transaction."), with the transaction left active and
    committable, both for an open stream (`BlobEngineTests`) and for an operation waiting for the
    writer lock (`BlobTransactionFailureTests`); the teardown cause "The session closed before the
    transaction ended." (for "The blob session closed before the transaction ended."); "The
    transaction is Committed; a committed transaction cannot roll back." and "The transaction is
    RolledBack." pinned exactly (the first had been a `Contains("Committed")`); the request seam's
    refusal, which named the deleted interface ("Blob sessions have no query language. Use the
    IBlobDatabase exposed by session.Database."), now names the session's container operations,
    and the text seam's refusal is pinned unchanged (`BlobTransactionFailureTests`).
  - *The engine.* The guard order, with the model's own name rule (a single file-name component)
    now in the cores after the base's checks, so `..` on a disposed engine is
    `ObjectDisposedException` and with a canceled token `OperationCanceledException`, where both
    were `ArgumentException`; the enumeration's disposal check at its call; the typed
    `TryGetDatabase` overload (`BlobEngineTests`). The disposal aggregate's message and the nested
    database aggregate, "One or more blob databases failed to close." (`BlobEngineTests`,
    `BlobEngineCompositionTests`). The blank `EngineName` refusal, the duplicate worker names
    (built-in and custom, which the model never checked), a blank worker name failing inside its
    factory, the pump threads named for their workers (a custom worker's in
    `BlobEngineCompositionTests`, the built-in flush worker's in `BlobWorkerResilienceTests`, which
    expected `blob-engine/WriteAheadFlush`), and the composition paths of §6.5
    (`BlobEngineCompositionTests`).
  - *Blob's server* (row 9). A start refused while the engine is `Faulted` disposes the listener
    once, before the refusal propagates, never binds it, and leaves the server stopped: a later
    start throws `ObjectDisposedException`, and the disposal releases nothing more
    (`BlobDatabaseServerTests.StartAsync_EngineFaulted_ShouldDisposeTheListenerAndStayStopped`;
    the existing disposed-engine test keeps its message). Before the base the refused start left
    the server inert, so a later start could retry (interim decision 31, pending owner
    confirmation, §7).
  - *Option B* (§6.6). `session.Database` is the unbound database: its container operations run in
    autocommit whatever transaction the session holds, where they ran in it; it creates a session
    after the session closed, where the view refused with "The blob session is closed."; and
    disposing it closes the database for every session, not the session, after which the engine
    refuses to reopen it with `ObjectDisposedException` until it is dropped or the engine is
    recreated (`BlobTransactionFailureTests.Database_OfASession_ShouldBeTheUnboundDatabase`). Both
    consequences §6.6 records for Documents reach Blob: a write through `session.Database`, or
    through a container it returned, while the session's transaction has written waits for that
    transaction's writer lock until the call's token is canceled, writes nothing and leaves the
    transaction committable (same suite,
    `CreateContainerAsync_ThroughSessionDatabaseAfterTransactionWrote_ShouldWaitForItsWriterLock`,
    a container create and a blob delete). A third consequence, found by the PR's review: the
    engine keeps a database its holder disposed registered (to refuse the reopen), and
    `IsOpen(BlobDatabase)` read only that registration, so the version-purge worker failed on the
    closed database's disposed coordinator every pass and the engine stayed `Faulted` for good,
    which made its server refuse every start, connection and handshake for every database. A
    directly disposed database did this before P4 too; option B made it reachable from the
    session's own property, where disposing the view only closed the session. The review's fix:
    `BlobDatabase.IsClosed`, `BlobDatabaseEngine.IsOpen` false for a closed database (and for its
    storage), and the version-purge worker skips a closed database in its pass and its trigger
    wait (the flush and write-back workers visit storages and tolerate a disposed storage's
    `ObjectDisposedException` through `IsOpen(BlobStorage)`; write-back already writes nothing
    for a disposed storage). As the review landed it, the checkpointer only tolerated a closed
    database's `ObjectDisposedException` through `IsOpen`; the follow-up of 2026-10-06 (#1289)
    made it skip one through `BlobCheckpointWorker.IsCheckpointDue`, false for a closed database,
    because a close that is not idle (a deferred undo's writer the close keeps in flight, #1226)
    leaves the closed storage due for a checkpoint it refuses with `StorageTransactionException`,
    which kept a checkpoint failure recorded before the close, and the engine `Faulted`, for good
    (§7, "The closed-database follow-up", "Blob's checkpointer").
    Asserted: `BlobDatabaseServerTests.DisposeAsync_SessionDatabase_ShouldLeaveTheEngineRunningAndItsServerServing`
    (20 ms worker intervals, half a second of passes over the closed database, then one more pass
    of each worker: every pass succeeds, no worker records a failure, the engine is `Running`, a
    server starts and serves a handshake and an upload to another database, and the closed one's
    reopen is still refused) and
    `BlobWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
    (a checkpoint failure recorded, then a close with a deferred undo's writer in flight: the
    failure ends and the engine runs again).
- *Not reachable from the model without a kernel hook, pinned by the root suite* (as for the other
  models): "… ended before the operation started; nothing was written." (where the model reported
  `COHDBB001` without a cause), and the session's "The session failed to close." aggregate (for
  "One or more blob operations failed to close.").
- *Unchanged for Blob, and asserted where the bases now carry it:* the refusal of an operation
  while the caller's commit or rollback runs ("… start the operation after it ends.", which the
  model already worded for operations; root suite); every commit of an aborted transaction
  reporting `COHDBB001` with the cause, a second commit included (now pinned with its inner
  exception), a commit after the teardown aborted an open stream reporting that operation's cause
  (`DisposeAsync_SessionWithOpenStream_ShouldAbortItAndReportItsCause`), and a commit after the
  session closed on a transaction a failed operation had already aborted reporting that operation's
  failure, which the base's teardown keeps rather than its own cause (the model already closed the
  transaction with a cause, as Documents did;
  `CommitAsync_AfterSessionClosed_ShouldReportWhyNothingCommitted(aborted: true)` beside the
  active case); the offline teardown's `Faulted` state (the model's `CloseAsync` recorded the
  cause too; `BlobStorageOperationsTests`); the worker disposal order (the model disposed its
  workers last attached first already); and the server's rethrow of an accept-loop failure after
  its drain (`Stop_FailedAcceptLoop_ShouldCleanUpActiveUploadsBeforeRethrowing`, unchanged). The
  database's disposal steps and their order are unchanged too (the coordinator, then the storage),
  but their path moved: `BlobDatabase.DisposeAsync` now awaits the coordinator's and the storage's
  `DisposeAsync` where the model's ran the synchronous `Dispose`, and the engine closes its
  databases after it has cleared them under its lock where it disposed them inside it, as Graph
  and Documents did.
- *Gone with the types, not a behavior a caller can reach:* `GetOwnershipAsync`'s refusals of a null
  container and of a foreign container implementation (§5.2).

**Sql at P4 (re-verified, then landed).** The model's copy was as "SQL in P4" above lists, with
one reading the plan had not spelled out: the session kept its transaction in a
`Stack<SqlTransactionScope>` whose `ActiveScope` popped an entry once its kernel state left
`Active`, so a transaction the kernel ended under its caller (its database dropped, or closed by a
holder, while the session held it) silently stopped being the session's transaction. The
transaction refused a commit or a rollback of an ended transaction by its kernel state ("Cannot
commit transaction in state '…'", "Cannot rollback transaction in state '…'", so a second
rollback failed), checked the offline database before that state, passed the commit's token to
`TransactionCoordinator.CommitAsync`, and had no end gate. All of it is deleted, the scope stack
with it (B7's savepoints will mark inside the one transaction; Sql `DESIGN.md`). The leaves supply
`GetKernelState` (the MVCC context's state), `CommitCoreAsync`/`RollbackCoreAsync` (the
coordinator calls with the model's translation: an unconfirmed commit keeps its type, a failure
the offline storage caused becomes `COHSQLT004`, then the deadlock and the kernel abort),
`GetOfflineRefusal` (`COHSQLT004`) and `CreateAbortedException` (`COHSQLT005`, below). SQL keeps
its per-statement contract: a failed statement writes nothing and leaves the transaction active,
so the model never calls `AbortAsync`, and a statement takes no session operation hold, as
KeyValuePair's commands take none. A statement inside the transaction is admitted through
`TryBeginOperation`/`EndOperation`, re-exposed as `TryBeginStatement`/`EndStatement`/
`CreateStatementRefusal`, which is what refuses a commit while a statement runs. Text BEGIN,
COMMIT and ROLLBACK keep their diagnostics where the transaction allows them: BEGIN as text on a
session with an open transaction first asks the base whether that transaction refuses work
(`ThrowIfTransactionRefuses`), so an aborted one answers `COHSQLT005` and an active one still
`COHSQLT001`; COMMIT and ROLLBACK without a transaction still answer `COHSQLT002`.

*The coded aborted error, chosen here as this section asked: `COHSQLT005`.* It is the next code
of SQL's transaction family (`COHSQLT001` to `COHSQLT003` are the transaction-control
diagnostics, `COHSQLT004` the offline refusal of #1243). Like the other models' aborted codes
(`COHDBK001`, `COHDBG007`, `COHDBB001`) it leads a `DatabaseException`'s message in the bases'
two wordings, "COHSQLT005: The session's transaction is aborted; statements are refused until it
is rolled back." and, for a commit, "COHSQLT005: The session's transaction is aborted and cannot
commit; nothing was committed.", each followed by " Cause: {message}" when the transaction
recorded a cause (the wire carries message text, not inner exceptions). A plain
`DatabaseException` reaches the wire as `ExecutionFailure`; `COHSQLT004`'s
`DatabaseOfflineException` stays `Unavailable`. Rejected: reusing `COHSQLT002`, a diagnostic
result rather than an exception that would tell the caller no transaction existed; and an
uncoded `DatabaseTransactionAbortedException`, which SQL already raises when the caller's own
commit loses to the kernel (a deadlock victim, a write conflict), so the two cases would read the
same. Sql `DESIGN.md` ("The transaction's end state machine") and `DIALECT.md` record the code.

Each change of this section that reaches SQL is accounted for:

- *Asserted in the model's suites:*
  - *The end gate and the repeatable rollback.* A rollback of a rolled-back transaction is a no-op,
    a rollback of a committed one fails with "The transaction is Committed; a committed
    transaction cannot roll back.", and a commit of a rolled-back one with "The transaction is
    RolledBack." (`SqlTransactionContractTests.RollbackAsync_AfterEnd_ShouldBeNoOpUnlessCommitted`;
    the model refused the second rollback). A commit while a statement of the transaction waits
    for another transaction's row lock is refused with "An operation of the transaction is still
    running; commit after it completes.", and the transaction commits once the statement completes
    (`CommitAsync_WhileAStatementRuns_ShouldBeRefusedAndLeaveTheTransactionActive`; the model's
    commit raced the statement into the coordinator).
  - *The commit token stops at the base.* A commit or rollback whose token is canceled before it
    starts throws `OperationCanceledException` and leaves the transaction active and committable
    (`EndAsync_TokenCanceledBeforeStart_ShouldLeaveTheTransactionActive`). `CommitCoreAsync` calls
    `TransactionCoordinator.CommitAsync(context)` without a token, so a started commit runs to its
    end; that half is read from the code, since no test can cancel inside the coordinator without
    a kernel hook.
  - *`Faulted` for a transaction the kernel ended.* Dropping the database while the session holds
    a transaction leaves it the session's `CurrentTransaction` with `State` `Faulted`; statements
    (typed, text and DDL) and BEGIN (typed and text) are refused with `COHSQLT005` until the caller
    rolls it back, twice without error, after which the session's BEGIN reports the dropped
    database (`ObjectDisposedException`) (`ExecuteAsync_TransactionEndedByTheKernel_ShouldBeFaultedAndRefuseWorkWithCohsqlt005`).
    A COMMIT, typed or as text, ends it with `COHSQLT005` and commits nothing, and a second commit
    reports "The transaction is {state}." (`CommitAsync_TransactionEndedByTheKernel_ShouldFailWithCohsqlt005`,
    both cases). Over the wire the statement and the COMMIT reach the client as `ExecutionFailure`
    carrying the code, the COMMIT ends the transaction, and the connection still answers a ping
    (`SqlTransactionControlTests.ExecuteAsync_WireTransactionEndedByTheKernel_ShouldCarryCohsqlt005AndKeepConnectionUsable`).
    On this path the state read `Faulted` before the bases too: the closing `TransactionManager`
    ends its active contexts as `Faulted`, and the model returned the kernel state. What changed is
    that the session keeps the transaction and refuses with a code; the model dropped it, so the
    statement ran in auto-commit and the text COMMIT answered `COHSQLT002`.
  - *The teardown cause.* Closing a session ends its transaction: a later rollback is a no-op, and a
    commit fails with "COHSQLT005: … nothing was committed. Cause: The session closed before the
    transaction ended." with that cause as its inner exception, and another session sees nothing
    of its writes (`DisposeAsync_SessionWithTransaction_ShouldEndItWithTheTeardownCause`, for
    "Cannot commit transaction in state 'RolledBack'.").
  - *BEGIN's refusal order.* BEGIN on a session with an active transaction is refused "already
    active" before the Serializable refusal, which it gets only on a session with none
    (`BeginTransactionAsync_WhileActive_ShouldBeRefusedBeforeTheIsolationLevel`, for "A
    transaction is already active on this session."); a closed session refuses BEGIN and both
    execute seams with "The session is closed." before the isolation level
    (`BeginAndExecute_OnClosedSession_ShouldRefuseWithOneMessage`, for "Session is not open.
    Current state: Closed."); on an offline database, BEGIN from the session that holds an open
    transaction is refused "already active" where it got `COHSQLT004`
    (`ExecuteAsync_OnOfflineDatabase_ShouldRunTheBaseChecksFirst`). On a kernel-ended transaction
    BEGIN refuses the transaction before the token (above).
  - *The token before the core.* `CreateSessionAsync`, BEGIN, both execute seams and
    `ApplySchemaAsync` refuse a canceled token before the offline refusal, which the model
    reported first, and the transaction's commit still gets `COHSQLT004`
    (`ExecuteAsync_OnOfflineDatabase_ShouldRunTheBaseChecksFirst`); both execute seams refuse it
    before a kernel-ended transaction's `COHSQLT005` (above).
  - *The engine.* The guard order on create, open, drop and the lookup (the name, disposal, the
    token) and the enumeration's disposal check at its call
    (`SqlEngineContractTests.Members_InvalidNameDisposedOrCanceled_ShouldCheckNameThenDisposalThenToken`;
    the model checked disposal first, `TryGetDatabase` did not check the name, open and drop
    observed no token, and the enumeration checked disposal at its first `MoveNextAsync`); the
    collation overload of `CreateDatabaseAsync`, which cannot await the base's member, makes the
    same checks in the same order with a null collation refused after the name and before
    disposal (pinned in the same test since the review: a blank name before disposal, a null
    collation before disposal, a canceled token on a live engine before anything is created); the
    typed members and the typed `TryGetDatabase` overload
    (`CreateDatabaseAsync_TypedMembers_ShouldHandOutTheSqlDatabase`); the disposal aggregate's message and the
    nested "One or more SQL databases failed to close." (`DisposeAsync_DatabasesFailToClose_ShouldReportThemAsOneComponent`,
    for "Engine disposal encountered failures."); the blank `EngineName` refusal, duplicate worker
    names, a blank worker name failing inside its factory (for "A worker must have a diagnostic
    name."), the pump threads named for their workers and the composition paths of §6.5
    (`SqlEngineCompositionTests`). `SqlWorkerResilienceTests` already asserted the flush worker's
    thread by the worker's name.
  - *The closed-database fault* (the Blob review's finding, §12, applied to SQL here).
    `SqlDatabase.IsClosed` (internal, the base's disposed flag), `SqlDatabaseEngine.IsOpen` false
    for a closed database, and every worker that visits a database skips a closed one: the flush,
    write-back and version-purge workers do not begin it, the purge worker's trigger wait ignores
    its deferred-undo retry, and the checkpointer never finds it due. With the fix removed both
    new tests fail: the version-purge worker throws `ObjectDisposedException` on the closed
    database's disposed `TransactionManager` every pass, the checkpointer on its disposed
    `StorageJournal` once it is due, and the engine goes `Faulted`.
    `SqlEngineContractTests.DisposeAsync_DatabaseOutsideTheEngine_ShouldBeSkippedByEveryWorkerUntilDropped`
    runs three passes of every worker over a closed database whose checkpoint is due: every pass
    succeeds, no worker records a failure, the engine stays `Running`, the database stays
    registered (an open hands back the closed instance, whose use throws
    `ObjectDisposedException`) until it is dropped, and a new database of that name is open.
    `SqlDatabaseServerTests.DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunningAndItsServerServing`
    closes one database through `session.Database` and one directly under 20 ms worker intervals:
    after half a second and one more pass of each worker, no worker failed, the engine is
    `Running`, and a new connection to the served database inserts and reads its rows. Neither
    test reaches the checkpointer's own guard (`!database.IsClosed` in `IsCheckpointDue`): an
    idle close leaves nothing due, and a closed storage's refusal under `IsOpen` is tolerated
    anyway. The review added the case the other three models carry, a close that keeps a writer
    in flight (#1226), whose storage refuses the checkpoint with `StorageTransactionException`
    rather than `ObjectDisposedException`:
    `SqlWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
    fails with the guard removed (the engine `Faulted` for the test's 30 seconds, the
    checkpointer holding the injected `IOException`) and passes with it.
- *Not reachable from the model without a kernel hook or a race, pinned by the root suite* (as for
  the other models): "… ended before the operation started; nothing was written." (a statement
  admitted between the session's check and the transaction's admission), "… start the operation
  after it ends." (a statement starting while the caller's own commit or rollback runs), and the
  session's "The session failed to close." aggregate (SQL's teardown let the transaction's
  failure out unwrapped).
- *Changed but not observable:* SQL disposed its checkpointer, then its factory workers last
  attached first; the base releases every worker last attached first, the built-in ones
  included. Only the checkpointer has a release hook among the built-in workers, so the order
  differs only between it and a custom worker, which no caller can observe.
- *Unchanged for SQL, and asserted where the bases now carry it:* the offline teardown (disposing
  an active transaction of an offline database touches nothing; `SqlStorageOperationsTests`),
  the server's lifecycle (row 9; `SqlDatabaseServerTests`), and the rollback's own #1226
  contract, its token observed only before it starts (`SqlTransactionRollbackTests`, unchanged
  apart from the retypes, like the other #1188, #1225 and #1226 suites).

### 6.5 Root-base state, attach semantics and typed accessors (rows 1, 5, 9, 12)

- **Attach.** `DatabaseEngine` owns `Workers` and `Servers`. Today a model fills them through the
  shared builder's `Complete(engine, attachWorker, attachServer)` callbacks
  (`Database/shared/DatabaseEngineBuilderState.cs:47-50`), and root-to-model grants are banned.
  So the base exposes non-virtual `protected` `AttachWorker` and `AttachServer`. The leaf's
  internal build path calls them and then calls `protected void CompleteComposition()`. Any later
  attach throws `InvalidOperationException`. An outside subclass could call them before freezing.
  That is the accepted cost of a `protected` root constructor (risk R6).
- **Typed accessors.** `DatabaseServer.Engine`, `DatabaseInstance.Engine` and
  `DatabaseSession.Database` are base fields behind non-virtual getters. The leaf declares
  `public new SqlDatabaseEngine Engine => _engine;` over its own typed field, so neither path makes
  a virtual call.
- **Async factories.** A `new` typed async member such as
  `public new ValueTask<SqlDatabaseSession> CreateSessionAsync(...)` awaits the base public member
  and casts once. It never calls the `Core` member directly (risk R3).

**At P3:** landed as described; the base also checks the attach preconditions
`DatabaseEngineBuilderState` checked (a product attached twice, a server fronting another engine).
**For P4.0 and P4:** a builder cannot call a protected member of the engine it built, so each
model's leaf exposes one internal composition method that calls `AttachWorker`, `AttachServer`
and `CompleteComposition`, and `DatabaseEngineBuilderState.Complete` takes it instead of the two
callbacks and drops the checks the base now makes. An engine created without its builder
(`SqlDatabaseEngine.Create(options)` and its siblings) completes its composition in `Create`.

**At P4.0 (re-verified, then landed).** The code matched this section, with two things the plan
had not spelled out: the compose method must still let the state run one factory at a time, and
the state's ownership test was also what kept it from disposing a product the engine owns.

- **The compose method.** `Complete(TEngine engine, Action<IEnumerable<TWorker>, IEnumerable<TServer>> compose)`.
  A leaf that derives from the base implements it as
  `internal void Compose(IEnumerable<DatabaseEngineWorker> workers, IEnumerable<DatabaseServer> servers)`:
  attach each worker, then each server, then `CompleteComposition()`. The state passes lazy
  sequences, and each request for the next product runs the next factory, so the order is the
  one the callbacks had (a factory, its attach, the next factory) and a factory still sees the
  products attached before it.
- **The checks.** `Complete` makes none of the attach checks; it refuses a null product ("A worker
  factory returned null.", "A server factory returned null."), checks the compose method's
  contract (since the P4.0 review, below) and disposes what a failed build leaves unowned. The
  ownership test stays, as the guard on that disposal (the contract check reuses it): when the
  compose method fails, the product it was attaching is disposed unless the engine's `Workers` or
  `Servers` already holds it. The base refuses a product it owns like any other attach failure,
  and that product (a repeated factory result, or a built-in worker a factory returned) is the
  engine's to dispose. The old test's third case, a product that is the engine itself, is
  dropped: it could never hold, because the five model engines are sealed and implement neither
  product interface, and `DatabaseEngine` is unrelated to the product bases.
- **The bridge.** `Complete(TEngine engine, Action<TWorker> attachWorker, Action<TServer> attachServer)`
  adapts an engine's two internal attach members into a compose method through a lambda and makes
  the two checks the base makes, with the same messages and disposal: a product attached twice is
  refused and left to the engine, a server whose `Context.Engine` is another engine is refused and
  disposed. At P4.0 all five builders use it, unchanged apart from the state's type arguments
  (`<SqlDatabaseEngine, IDatabaseEngineWorker, IDatabaseServer>` and its siblings). Their
  interface-typed `AddWorker` and `AddServer` pass the factory to the typed state through delegate
  variance (`Func<IDatabaseEngine, T>` converts to `Func<SqlDatabaseEngine, T>`), not through a
  lambda, so a null factory still throws `ArgumentNullException` at registration.
- **Each model PR in P4** adds the leaf's `Compose`, builds through an internal creation path that
  leaves composition open (`Create` completes it), calls `_state.Complete(engine, engine.Compose)`,
  deletes the engine's internal attach members, and moves the state's type arguments to
  `<…DatabaseEngine, DatabaseEngineWorker, DatabaseServer>`. The model's builder-test doubles
  derive from the bases in the same PR (§6.9). The last model PR deletes the bridge overload.
- **Evidence.** Every model's builder and composition tests pass through the bridge, which runs the
  new `Complete` (§7). No engine in the repository derives from the base yet, so a scratch probe
  outside the repository composed one that does, through such a `Compose`: products attached in
  order, a later factory saw the earlier product, and the engine froze; a repeated server and a
  returned built-in worker were refused and disposed once, by the engine; a foreign server and a
  worker with a duplicate name were refused and disposed by the state; a null product and a
  failing factory disposed the engine; and a failure to dispose a refused server was aggregated
  with the refusal. The KeyValuePair PR's builder tests are the first in-repository run of that
  path.
- **The contract, enforced (P4.0 review).** The disposal on failure rests on the compose method's
  contract: it reads the workers once and to the end, then the servers once and to the end, and
  attaches each product before it requests the next, so only the product last handed out can be
  unattached. As landed the state trusted that, and a compose that broke it failed silently: one
  that skipped the servers dropped every server factory, one that read a sequence twice ran its
  factories twice, and one that buffered a sequence leaked every product it had not attached,
  because the drained iterator had cleared `_pending`. The state now checks the contract as the
  compose method reads. It tracks one progress value through the only order allowed (workers
  read, workers attached, servers read, servers attached), and it fails the build with an
  `InvalidOperationException` when a sequence is read twice ("The compose method read a product
  sequence twice."), when a server is requested before every worker is attached ("… requested a
  server before it attached every worker."), when the method reads past a product it did not
  attach ("… read past a product it did not attach.", checked before each later factory and at
  the end of each sequence), or when it returns before it read both sequences to the end ("…
  returned before it attached every product."). Each check throws while the unattached product is
  still pending, so the usual cleanup disposes it, and no later factory runs. A correct compose
  method, the bridge's included, never reaches a check; the only new cost on a good build is one
  ownership scan per product. The state's `<exception>` docs now say a factory's, the compose
  method's or an attach's own exception is rethrown unchanged, and the bridge lists SQL's
  `ArgumentException` for a blank worker name.
- **In-repository evidence (P4.0 review).** The interleaving the lazy sequences exist to keep is
  pinned on both paths: `SqlEngineCompositionTests.Build_Factories_ShouldSeeEveryProductAttachedBeforeThem`
  (the bridge) and its KeyValuePair twin (the compose method) record that the second worker
  factory sees the first worker, the first server factory sees both workers and the second server
  factory sees the first server; an eager `Produce` fails both. A refused server whose cleanup
  fails is aggregated after the refusal on both paths
  (`WrongServerEngineFailingDisposal_ShouldAggregateTheRefusalAndDisposeTheEngine`,
  `Build_RefusedServerFailsToRelease_ShouldAggregateTheRefusalAndTheCleanup`). KeyValuePair's
  `Complete_ComposeBreaksTheContract_ShouldFailAndReleaseEveryProductOnce` drives the state
  directly with `KeyValueDatabaseEngine.Compose` misused five ways (servers skipped, workers
  buffered, servers buffered, workers read twice, servers requested after one worker) and checks
  the message, that no later factory ran, and that every product made was released once, by the
  engine when attached and by the state when not; all five cases fail against the state as
  landed.

**Sql at P4 (landed).** The engine follows the shape the other four landed:
`internal void Compose(IEnumerable<DatabaseEngineWorker>, IEnumerable<DatabaseServer>)`,
`internal static SqlDatabaseEngine CreateUncomposed(options)` (which validates the options, a blank
`EngineName`, the nesting limit, the buffer pool and the checkpoint settings, and leaves
composition open) and `Create` over both; the constructor attaches its five built-in workers last,
after every field they observe is set, through the base's `AttachWorker`; the internal attach
members, the worker, server and custom-worker lists, the stop token, the pump threads and the
model's state fold are gone. The builder composes through `_state.Complete(engine, engine.Compose)`
(since the review, with `SqlDatabaseEngine.ReleaseRefusedWorkerAsync` as a third argument, as
every model builder passes its engine's; row 7), and with it the last caller of the bridge overload went, so the overload and its `ThrowIfAttached`
helper are deleted from `DatabaseEngineBuilderState` (the P4.0 plan's "The last model PR deletes
the bridge overload"). `SqlEngineCompositionTests`, rewritten from Blob's suite with the five
built-in workers, runs every path `KeyValueEngineCompositionTests` runs (the compose-contract cases
against `SqlDatabaseEngine.Compose`, the pump thread names, the failing factory and `AddSql`'s
premature-build compensation) plus the two SQL-specific cases the old suite had: a refused
server whose stop yields does not resume on the caller's synchronization context, and a
cancellation callback that throws during disposal still joins the pump before the worker is
released. The typed accessors are Graph's set: the engine's three database members as `new`
members and `TryGetDatabase` as a typed overload, plus SQL's collation overload of
`CreateDatabaseAsync`, which makes the base's checks itself; the database's `Engine` and
`CreateSessionAsync`; the session's `Database`, `CurrentTransaction` and both
`BeginTransactionAsync` overloads; the server's `Engine`; and the server session's covariant
`DatabaseSession`. The database cores check disposal under the engine's lock, and
`GetDatabasesCore` is the model's iterator.

**Blob at P4 (landed).** The engine, the builder, the strategy, the options and the four
workers were, before this PR, Documents' pre-P4 files with the model's names (a diff with the
names mapped differs only in namespaces and in the verb's name, `AddBlob`), so each follows the
Documents shape below with them: `internal void Compose(...)`,
`internal static BlobDatabaseEngine CreateUncomposed(options)` (which validates the options, a
blank `EngineName` included, and leaves composition open) and `Create` over both; the
constructor attaches the four built-in workers last, after the root path is created, through the
base's `AttachWorker`; the internal attach members, the worker, server and thread lists, the stop
token and the model's pump are gone. `BlobEngineCompositionTests` and `BlobApplicationBuilderTests`
(Documents' suites with the model's names; the builder suite differed from Documents' pre-P4
one only in names and comments) together run every path `KeyValueEngineCompositionTests` runs:
the compose-contract cases against `BlobDatabaseEngine.Compose`, the pump thread names, the
failing factory and `AddBlob`'s premature-build compensation. The typed accessors are Graph's
set: the engine's three database members as `new` members and `TryGetDatabase` as a typed
overload, the database's `Engine` and `CreateSessionAsync`, the session's `Database`,
`CurrentTransaction` and both `BeginTransactionAsync` overloads, the server's `Engine`, and the
server session's covariant `DatabaseSession`. The model's name rule runs in the cores, after the
base's checks and under the engine's lock with a disposal check of its own, and
`GetDatabasesCore` is the model's iterator, which opens each stored name through the engine's
public `OpenDatabaseAsync`, as in Graph and Documents. The server's tests and Blob.Client's read
`BlobDatabaseServer.Sessions` instead of `Context.Sessions`.

**Documents at P4 (landed).** The engine, the builder, the strategy, the options and the four
workers were, before this PR, Graph's pre-P4 files with the model's names (a diff with the names
mapped is empty), so each follows the Graph shape below with them: `internal void Compose(...)`,
`internal static DocumentDatabaseEngine CreateUncomposed(options)` (which validates the options, a
blank `EngineName` included, and leaves composition open) and `Create` over both; the constructor
attaches the four built-in workers last, after the root path is created, through the base's
`AttachWorker`; the internal attach members, the worker, server and thread lists, the stop token
and the model's pump are gone. `DocumentEngineCompositionTests` (Graph's suite with the model's
names) and `DocumentApplicationBuilderTests` together run every path `KeyValueEngineCompositionTests`
runs: the compose-contract cases against `DocumentDatabaseEngine.Compose`, the pump thread names,
the failing factory and `AddDocuments`' premature-build compensation. The typed accessors are
Graph's set without the server's (the engine's three database members as `new` members and
`TryGetDatabase` as a typed overload, the database's `Engine` and `CreateSessionAsync`, the
session's `Database`, `CurrentTransaction` and both `BeginTransactionAsync` overloads); Documents
has no server and so no server session. The model's name rule runs in the cores, after the
base's checks and under the engine's lock with a disposal check of its own, and
`GetDatabasesCore` is the model's iterator, which opens each stored name through the engine's
public `OpenDatabaseAsync`, as in Graph.

**Graph at P4 (landed).** The engine follows the KeyValuePair shape below: `internal void Compose(...)`,
`internal static GraphDatabaseEngine CreateUncomposed(options)` (which validates the options, a
blank `EngineName` included, and leaves composition open) and `Create` over both; the constructor
attaches the four built-in workers last, after the root path is created, through the base's
`AttachWorker`; the internal attach members, the worker, server and thread lists, the stop token
and the model's pump are gone. `GraphEngineCompositionTests` and `GraphApplicationBuilderTests`
together run every path `KeyValueEngineCompositionTests` runs: the former the compose-contract
cases against `GraphDatabaseEngine.Compose` and the rest, plus the pump thread names; the latter
the failing factory (`FailedServerFactory_ShouldDisposeEarlierComponentsAndFreezeBuilder`) and
`AddGraph`'s premature-build compensation
(`ConfigurationThatBuildsPrematurely_ShouldNotLeakItsEngine`). The typed accessors are
KeyValuePair's set (the engine's three database members as `new` members and `TryGetDatabase` as
a typed overload, the database's `Engine` and `CreateSessionAsync`, the session's `Database`,
`CurrentTransaction` and both `BeginTransactionAsync` overloads, the server's `Engine`, and the
server session's covariant `DatabaseSession`). Two readings of the model's code: its name rule (a
database name is a single file-name component, because its files live in a directory named for
it) runs in the cores, after the base's checks, and still under the engine's lock with a
disposal check of its own, so a create that raced the disposal cannot add a database after
`DisposeAsyncCore` closed them; and `GetDatabasesCore` is the model's iterator, which opens each
stored name through the engine's public `OpenDatabaseAsync`, so each open runs the base's checks.
The server's tests read `GraphDatabaseServer.Sessions` instead of `Context.Sessions`.

**KeyValuePair at P4 (landed).** The engine has `internal void Compose(IEnumerable<DatabaseEngineWorker>, IEnumerable<DatabaseServer>)`
and `internal static KeyValueDatabaseEngine CreateUncomposed(options)`, which validates the options
(a blank `EngineName` included) and leaves composition open; `Create` calls it and then
`CompleteComposition()`. The constructor attaches the five built-in workers last, after every field
they read, through the base's `AttachWorker`, which starts each pump at once; the internal
`AttachWorker`/`AttachServer`, the worker and server lists and the model's pump threads are gone.
`KeyValueEngineCompositionTests` runs every path the P4.0 probe ran, in the repository: order and
a later factory seeing the earlier product, the freeze (a `Compose` after `Build` or `Create` is
refused), a repeated server and a returned built-in worker refused and released once by the
engine, a foreign server and a duplicate worker name refused and released by the state, a blank
worker name failing inside its factory, null products, a failing factory, the premature-build
compensation of `AddKeyValue`, and a component's disposal failure reported in the engine's one
aggregate. The P4.0 review added the probe path the landing had left out (a refused server whose
cleanup fails, aggregated after the refusal), the full interleaving (servers after every worker,
a server factory seeing the server before it) and the compose-contract cases (§6.5, "The
contract, enforced").

The typed accessors, as landed: `public new` members over the base's public members for the
engine's `CreateDatabaseAsync`, `OpenDatabaseAsync` and `GetDatabasesAsync` (the leaf's core
returns its own `IAsyncEnumerable<KeyValueDatabase>`, which the covariant interface lets the base
hand out unchanged, so the typed member casts the sequence once rather than each item), the
database's `Engine` and `CreateSessionAsync`, the session's `Database`, `CurrentTransaction` and
both `BeginTransactionAsync` overloads, and the server's `Engine`. `CurrentTransaction` is not
construction-fixed state: its `new` getter reads the base's public getter and casts once, on
the same terms as an async factory (rule 7). `TryGetDatabase` is typed as an overload, not a
`new` member: `TryGetDatabase(DatabaseName, out KeyValueDatabase)` differs from the base's
`out DatabaseInstance` in a parameter type, so it hides nothing (`new` would raise CS0109). It
calls the base's public member and casts once, so the name and disposal checks run in their
order. It is not ambiguous: overload resolution drops a base type's candidates once a method of
the derived type applies (C# §12.8.10.2), so an `out var` or `out _` call on the engine binds the
typed overload, and an explicitly typed `out DatabaseInstance` binds the base's (P4 review; the
landing first kept the base type on the contrary belief, and a warnings-as-errors probe disproved
it). The later model PRs type their lookup the same way. The server session overrides `DatabaseSession` covariantly (`KeyValueDatabaseSession?`), but the
leaf is internal, so `DatabaseServer.Sessions` stays typed `DatabaseServerSession`.

### 6.6 Blob and Documents sessions: option B (rows 54, 61)

`BlobSessionDatabase` and `DocumentSessionDatabase` dispose the **session**
(`BlobDatabaseSession.cs:244-245`, `DocumentDatabaseSession.cs:292-293`), while the unbound
database disposes the database. Folding both into one sealed type would give `Dispose` two
meanings. Under option B:

- `BlobDatabaseSession` and `DocumentDatabaseSession`, now public and sealed, expose the container
  and collection operations directly;
- `session.Database` returns the unbound `BlobDatabase` or `DocumentDatabase`;
- the session-bound view types are deleted.

Studio's `BlobWorkspace` and `DocumentWorkspace` and the Blob and Documents wire servers move to
the session operations in the same model PR.

**Documents at P4 (re-verified, then landed).** The cited lines had moved: the view
(`DocumentSessionDatabase`) was at `Internal/DocumentDatabaseSession.cs:271` and its `Dispose` and
`DisposeAsync` at `:299-300`. Read with the code, the view did three things the section did not
name, and each has a home:

- *Its four collection operations ran in the session's transaction.* They moved onto the session
  as public members (`DocumentDatabaseSession.CreateCollectionAsync`, `GetCollectionAsync`,
  `DropCollectionAsync`, `GetCollectionsAsync`), each a statement of the session that returns a
  collection bound to it, so the session-bound handle the view returned keeps its binding.
- *The unbound database kept the interface's own four collection operations,* which run in
  autocommit outside any session. They stay on `DocumentDatabase`: option B removes only the
  view, and `session.Database` is now that database, so a collection operation called through it
  no longer joins the session's transaction.
- *Its `CreateSessionAsync` refused a closed session* ("The document session is closed.") before
  it created a session of the database. `session.Database.CreateSessionAsync()` is now the
  database's own and creates a session after the session closed.

Disposal now has one meaning per type: disposing the session closes the session, and disposing
`session.Database` closes the database (the view's disposal closed the session). That reaches
every session of the database, and the engine keeps the disposed instance registered, so
`OpenDatabaseAsync` refuses it with `ObjectDisposedException` until it is dropped or the engine is
recreated, as it always did for a directly disposed database; option B lets any session holder
reach it. A second consequence of the database's autocommit operations: a write through
`session.Database` (`CreateCollectionAsync`, `DropCollectionAsync`) while the session's explicit
transaction has written waits for that transaction's writer lock, because the engine has one
writer at a time and the write is not part of the transaction, so the caller that awaits it
before ending the transaction waits until the call's token is canceled (the lock manager sees no
cycle). Inside a transaction, use the session's own collection operations; the session, database
and Documents `DESIGN.md` remarks say so. All of these changes are asserted
(`DocumentTransactionFailureTests`: the option-B test, which also pins the refused reopen, and
`CreateCollectionAsync_ThroughSessionDatabaseAfterTransactionWrote_ShouldWaitForItsWriterLock`).
The Blob review found a third consequence, which reached Documents too: the workers treated the
closed database as open, and the version-purge worker's failure on its disposed coordinator left
the engine `Faulted` for good. The follow-up of 2026-10-06 fixed it as Blob's review did (§6.4's
Documents accounting, §7 "The closed-database follow-up").
Documents has no wire server, so only Studio's `DocumentWorkspace` moved: it runs its collection tools on the session (a cast
from `ModelWorkspace`'s root-typed session to `DocumentDatabaseSession`, which phase 7's retype
removes) where it cast the session's database to `IDocumentDatabase`.

**Blob at P4 (re-verified, then landed).** The cited lines had moved: the view
(`BlobSessionDatabase`) was at `Internal/BlobDatabaseSession.cs:223` and its `Dispose` and
`DisposeAsync` at `:251-252`. Read with the code, the view did what the Documents view did, and
each part has the same home:

- *Its four container operations ran in the session's transaction.* They moved onto the session
  as public members (`BlobDatabaseSession.CreateContainerAsync`, `GetContainerAsync`,
  `DropContainerAsync`, `GetContainersAsync`), each an operation of the session that returns a
  container bound to it, so the session-bound handle the view returned keeps its binding.
- *The unbound database kept the interface's own four container operations,* which run in
  autocommit outside any session, and so do the operations of a container it returns. They stay
  on `BlobDatabase` (interim decision 32, pending owner confirmation, §7): option B removes only
  the view, and `session.Database` is now that database, so a container operation called through
  it no longer joins the session's transaction.
- *Its `CreateSessionAsync` refused a closed session* ("The blob session is closed.") before it
  created a session of the database. `session.Database.CreateSessionAsync()` is now the database's
  own and creates a session after the session closed.

Both consequences recorded for Documents above reach Blob, and are asserted
(`BlobTransactionFailureTests`: `Database_OfASession_ShouldBeTheUnboundDatabase`, which also pins
the refused reopen, and
`CreateContainerAsync_ThroughSessionDatabaseAfterTransactionWrote_ShouldWaitForItsWriterLock`):
disposing `session.Database` closes the database for every session and the engine refuses to
reopen it with `ObjectDisposedException` until it is dropped or the engine is recreated; and a
write through `session.Database`, or through a container it returned (a create, a drop, an upload
or a delete), while the session's explicit transaction has written waits for that transaction's
writer lock until the call's token is canceled (the self-wait the session, database and Blob
`DESIGN.md` remarks document). Inside a transaction, the session's own container operations are
the ones to use. The PR's review found a third consequence: the engine's workers treated the
closed database as open (the engine keeps it registered to refuse the reopen), so the
version-purge worker failed on its disposed coordinator every pass, the engine stayed `Faulted`
for good, and the Blob server refused every start, connection and handshake, for every database.
A directly disposed database did this before P4 too; option B put it behind the session's own
property. Fixed in the review: the engine's `IsOpen` is false for a closed database and its
storage, and the version-purge worker skips a closed database, so the engine stays `Running` and
its server keeps serving its other databases
(`BlobDatabaseServerTests.DisposeAsync_SessionDatabase_ShouldLeaveTheEngineRunningAndItsServerServing`;
§6.4's Blob accounting). The other models' engines carried the same `IsOpen` shape; the
follow-up of 2026-10-06 fixed it for Documents (reached through `session.Database` here as well),
Graph and KeyValuePair (§6.4's accounting of each, §7 "The closed-database follow-up"), the Sql
PR fixed Sql's, and a later follow-up the same day made Blob's checkpointer skip a closed database
through `BlobCheckpointWorker.IsCheckpointDue` (§12; §7, "The closed-database follow-up", "Blob's
checkpointer"). The Blob wire server runs the bound session's own
container operations, where it
cast the session's database to `IBlobDatabase`, so its exchanges still join the session's
transaction (a host-opened one included); Studio's `BlobWorkspace` runs its container and blob
tools on the session (a cast from `ModelWorkspace`'s root-typed session to `BlobDatabaseSession`,
which phase 7's retype removes) where it cast the session's database to `IBlobDatabase`.

### 6.7 Sql.Schema behind an opaque `SqlSchema` (rows 87 to 101)

- **The records stay internal.** The nine declaration records are positional records
  (`Internal/SqlSchemaBuilder.cs:103-289`). A positional record's primary constructor cannot be
  made internal, and a public `with` lets callers clone around validation.
- **Only Sql.Schema's tests read them.** [Certain] Outside `Sql.Schema/src`, `ISqlSchema*` is used
  only by `SqlSchemaTests.cs` and `CompiledSchemaTests.cs`.
- **The public surface.** `public static class SqlSchema` (`SqlSchema.cs:10`) becomes
  `public sealed class SqlSchema` with an internal constructor, `Name`, and an instance
  `Compile()`. The static `Create(name, configure)` and `Compile(name, configure)` stay.
- **The SDK still matches.** The SDK extractor matches those calls by the containing type's name
  (`CSharpSchemaExtractor.cs:710`), which a non-static class keeps.
- **Visibility changes.** `SqlSchemaCompiler` becomes internal, and Sql.Schema gains
  `[assembly: InternalsVisibleTo("Assimalign.Cohesion.Database.Sql.Schema.Tests")]` in a new
  `src/Properties/AssemblyInfo.cs`. The critique assumed this grant already existed; it does not.
- **The builders.** The four builder interfaces and `ISqlTriggerContext` become sealed classes.
  The SDK matches them by metadata-name strings (`CSharpSchemaExtractor.cs:20-23`, `:729-730`;
  `CSharpExpressionCanonicalizer.cs:480`), so those strings change in the same commit.
- **Gate.** The SDK tests run against refreshed canonical packs, because a stale pack hides a real
  failure.

**At P4 (re-verified, then landed).** The section held against the code, with three readings:

- *The builders' file.* The four builders, their records and `SqlSchemaModel` shared
  `Internal/SqlSchemaBuilder.cs`. The builders moved to their own files in the `RootNamespace`
  (`SqlSchemaBuilder.cs`, `SqlTableBuilder.cs`, `SqlTypeBuilder.cs`, `SqlPrincipalBuilder.cs`,
  `SqlTriggerContext.cs`); the records stayed, in the renamed `Internal/SqlSchemaDeclaration.cs`,
  with `SqlSchemaModel` renamed `SqlSchemaDeclaration` so "model" means only `EngineModel`. The
  builder classes had been the internal implementations: `SqlSchemaTableBuilder<TRow>` became
  `SqlTableBuilder<TRow>`, `SqlSchemaTypeBuilder` `SqlTypeBuilder` and
  `SqlSchemaPrincipalBuilder` `SqlPrincipalBuilder`, each with an internal constructor and the
  interface's documentation.
- *The opaque type.* `SqlSchema` exposes `Name`, the instance `Compile()` and the two statics;
  `Compile(name, configure)` is `Create(name, configure).Compile()`, so both paths validate and
  hash alike (`SqlSchemaTests.Compile_FromDeclaration_ShouldMatchOneStepCompilation`). The
  declaration is an internal `Declaration` property the compiler and the tests read.
- *What a caller can observe.* The trigger context's assembly-qualified identity is part of a
  trigger's canonical expression (`lambda<…>(p0:…SqlTriggerContext,…)` and the `Audit` call's
  method id), so the hash of a schema that declares a trigger changed with the rename; nothing has
  shipped (§1). The builder's `Table<T>(name, …)` and `Extension(name, …)` report a blank name
  with `ParamName` `name`, the documented parameter, where the former implementation reported
  `tableName` and `extensionName` (`SqlSchemaTests.Create_WithInvalidArguments_ShouldRejectDeclaration`).
  Nothing else changed: the extractor matches the new names (`SchemaBuilderType`,
  `TableBuilderType`, `TypeBuilderType`, `PrincipalBuilderType` and the ``SqlTableBuilder`1``
  metadata name in `CSharpSchemaExtractor.cs`, `SqlTriggerContext` in
  `CSharpExpressionCanonicalizer.cs`), and the SDK's parity test
  (`CompileDatabaseSchemaTaskTests`), which declares a custom type, tables with a reference, a
  function, a trigger and a principal, compiles the same canonical document through the runtime and
  through the SDK.

### 6.8 One Diagnostics convention: never null (D12)

`QueryResult.Diagnostics` is nullable (`Execution/src/QueryResult.cs:33`), and its ten overrides
return `null` when they have nothing to report. #1228 made `GraphSchemaResult<T>.Diagnostics`
non-null and empty instead (`Graph/src/GraphSchemaResult.cs:28`; commit `9eee083d` cites the .NET
design guideline against null collection properties). The area adopts the #1228 form:

- Diagnostics and other result collections return an empty collection, never `null` (rule 9 in
  `database-area.md`). A member where `null` means "absent" is outside the rule and keeps its
  meaning: `QueryRequest.Parameters` and its four overrides, `SqlInsertExpression.Columns`
  (`Sql.Language/src/Expressions/SqlInsertExpression.cs:44`, "the column list, if specified"),
  `SqlInExpression.Values` (`SqlInExpression.cs:38`) and
  `BTreeIndexManagerOptions.ExistingIndexes` (`Indexing/src/BTreeIndexManagerOptions.cs:36`);
- P1 deletes one override, `QueryStatementResult` (`Execution/src/QueryStatementResult.cs:36`),
  with the pipeline (row 15);
- P8 changes `QueryResult.Diagnostics` to `IReadOnlyList<Diagnostic>` and updates the remaining
  nine overrides, plus the seven test files that assert `null` (Graph `GqlUnknownTokenWarningTests`,
  `GqlLabelDirectionExecutionTests`, `GraphPathsQueryTests`; Sql `SqlExpressionDepthExecutionTests`,
  `SqlTransactionControlTests`, `SqlStatementCompletenessExecutionTests`,
  `SqlSubqueryBindingDiagnosticTests`).

[Likely] No wire change follows: protocol 1.0 carries no Diagnostics frame for a successful
statement (#1228).

### 6.9 `InternalsVisibleTo` additions and foreign test rewrites

**Grants added**, each to the project's own test assembly only:

| Grant | Phase | Needed by |
|---|---|---|
| Transactions → `Assimalign.Cohesion.Database.Transactions.Tests` (**already present** at P1: `src/Properties/AssemblyInfo.cs` landed with the deferred-undo backoff's internal clock, so P1 adds no grant) | P1 | `FailingCommitLog` (`TransactionManagerTests.cs`); `ControlledLog` (`TransactionManagerRollbackTests.cs`); `ControlledVersionStore` (from P2); tests that call the now-internal log factories and the internal `TransactionManager.Create` overload; the coordinator's three internal test hooks (below) |
| Sql.Schema → `Assimalign.Cohesion.Database.Sql.Schema.Tests` (new `src/Properties/AssemblyInfo.cs`) | P4 (Sql) | `SqlSchemaTests.cs` and `CompiledSchemaTests.cs`, which read the internal declaration model. **Landed at P4**, the package's first grant; the tests also call the internal `SqlSchemaCompiler` |

Every other derivation goes through a protected constructor, or through a grant that already
exists. [Certain] The existing grants cover Storage, Sql, Sql.Catalog, Sql.Storage, Sql.Language,
KeyValuePair, KeyValuePair.Catalog, KeyValuePair.Storage, Graph, Graph.Language, Documents,
Documents.Language, Blob and Hosting.

**Foreign test rewrites.** No grant can reach these tests:

- **Indexing.Tests** `TestObjects/IndexTestHarness.cs`:
  - `:26-27` and `:39-40` call `TransactionManager.Create` without the log (P1);
  - `:52-54` retype `Manager`, `LockManager` and `IndexManager` (P1, P2);
  - `:18` stops implementing `IStorageTransactionSource` and supplies the delegate (P2);
  - `BTreePageFormatTests.cs:53` supplies the delegate too (P2).
  - **As landed (P2):** all four, plus the registry casts in the Indexing tests, which call
    `ExportRegistrations()` on the sealed manager directly.
- **Sql.Tests** `SqlMvccBindingTests.cs:63`, `:71` read `database.Coordinator.Manager.OldestActive`.
  They compile unchanged, because `OldestActive` stays public on the sealed type; P1 verifies it.
- **Transactions.Tests, P1: the coordinator hooks (rows 28 and 32).** P1 retypes
  `TransactionCoordinator` to `Storage` and `StorageJournal`. The members three doubles
  intercepted are public and non-virtual there (`Storage.ReserveTransactionSequence`,
  `Storage.Checkpoint`, `StorageJournal.AppendRollback`), and `StreamJournal` is sealed. The
  doubles also stopped compiling, because `IStorage` was in their base lists:
  - `CoordinatorStorage` (`TransactionCoordinatorRecoveryTests.cs`) re-implemented
    `IStorage.Checkpoint` and `IStorage.ReserveTransactionSequence` to fire `BeforeCheckpoint`
    and `SequenceReserved`;
  - `RollbackStorage` (`TransactionCoordinatorRollbackTests.cs`) re-implemented
    `IStorage.Checkpoint` to fire `BeforeCheckpoint`;
  - `FaultInjectingJournal` (same file) decorated the storage's journal and threw from
    `AppendRollback`.

  **Seam decision (re-verified at P1 against #1251 to #1253; landed as planned).**
  `TransactionCoordinator` owns three internal hooks, reached through the existing
  Transactions → Transactions.Tests grant. Each sits at the call the double intercepted, inside
  the same gate, so the tests keep their interleavings:
  - `BeforeCheckpoint(long[] writers)`, invoked under the append gate just before
    `storage.Checkpoint(writers)`, inside the same `try`, so a throw still resets the journaled
    set in the `finally`;
  - `SequenceReserved()`, invoked in the manager's sequence allocator after
    `Storage.ReserveTransactionSequence`;
  - `BeforeAbortRecord(long sequence)`, invoked inside the `try` of the abort record's append; a
    throw rejects the record, and the `finally` still drops the sequence from the active set.

  `FaultInjectingJournal` is deleted; an `AbortRecordProbe` over `BeforeAbortRecord` keeps its
  `FailRollbackRecords`, `AppendedRollbacks` and `RejectedRollbacks`. `CoordinatorStorage` and
  `RollbackStorage` drop `IStorage` and their explicit re-implementations; only the record-space
  split (C9) waits for P2. The `RecordCount(IStorage)` and `CountRecords(IStorage)` helpers
  (`TransactionCoordinatorRecoveryTests.cs`, `TransactionCoordinatorRollbackTests.cs`,
  `TransactionCoordinatorCheckpointCrashTests.cs`, `TransactionCoordinatorScrubCrashTests.cs`,
  `RecordSpaceVersionStoreTests.cs`) and the doubles' `Log` properties retype to `Storage` and
  `StorageJournal`.

  **Rejected seams.** `protected virtual` observer hooks on `Storage` would add protected surface
  to a public base purely for tests (`general-rules.md`, "Adding a public API to the producer
  purely to serve one consumer"). A `StorageJournal`-derived decorator cannot forward to another
  instance's protected frame cores (CS1540), and `StorageJournal` has a `private protected` core
  besides, and a test cannot hand `Storage` its own journal: `Storage` builds its `StreamJournal`
  itself. **Corrected at P1:** the plan also rejected a stream-level trigger, because it would tie
  the tests to the frame layout #1236 was rewriting. #1252 changed that premise. Every journal
  append is now a write into a user-space buffer, so a failed journal write is a failed *drain*,
  and a failed drain takes the storage offline; the engines' `FaultInjectingJournal*StorageStrategy`
  doubles and the coordinator tests below now fail the journal's backing stream
  (`FaultingMemoryStream.FailWrites`) without reading a frame. P1 therefore uses a stream-level
  trigger wherever the scenario is "a journal write fails", and keeps a coordinator hook only
  where no stream failure reproduces the scenario: an interleaving at the checkpoint or the
  sequence reservation, and an abort record rejected while the storage stays online.

  **The tests the hooks carry** (re-counted at P1; the plan's list predated #1242, #1252 and
  #1254):
  - `BeforeCheckpoint`: `Checkpoint_ConcurrentLifecycleAppend_ShouldPreserveClassification` (both
    cases), `Checkpoint_ReadersAndWriters_ShouldAnchorOnlyTheWriters`,
    `TryCheckpoint_GateHeldByAStatement_ShouldDeferTheCheckpointToTheStatementsEnd` and
    `TryCheckpoint_DeferredCheckpointFails_ShouldThrowFromTheNextCallNotTheStatement`
    (`TransactionCoordinatorRecoveryTests.cs`); through the rollback tests' `Checkpoint` helper,
    `RollbackAsync_JournalRejectsAbortRecord_ShouldReleaseWriterForTheNextOne`,
    `RollbackAsync_UndoFails_ShouldHoldLocksAndVisibilityUntilThePurgePassCompletesIt` and
    `DisposeAsync_UndoStillFailsAndStorageClosesCleanly_ShouldLeaveTheWriterForRecoveryToScrub`
    (the `checkpointFirst` case);
  - `SequenceReserved`: `Checkpoint_ConcurrentLifecycleAppend_ShouldPreserveClassification` (the
    begin case);
  - `BeforeAbortRecord`: `RollbackAsync_JournalRejectsAbortRecord_ShouldReleaseWriterForTheNextOne`,
    `RollbackAsync_UndoFails_ShouldHoldLocksAndVisibilityUntilThePurgePassCompletesIt` and
    `Recovery_WriterWithoutAbortRecord_ShouldBeClassifiedAbortedAndScrubbed` (the completed-undo
    case; the deferred case asserts that no abort record was requested).

  `RollbackAsync_UndoBracketJournalWriteFails_ShouldDeferTheUndoAndLeaveCheckpointsRunning`, which
  this list used to name, no longer exists: #1252 renamed it and split it into
  `RollbackAsync_StorageWentOfflineBeforeTheUndo_ShouldDeferTheUndoAndLeaveTheWriterToRecovery`
  and `RollbackAsync_UndoRecordsLostAtTheNextDrain_ShouldGoOfflineAndLeaveTheWriterToRecovery`,
  which use the stream-level trigger and need no hook.

  Each test asserts that its hook fired, so a hook that is silently never called cannot pass. The
  rollback tests' `Checkpoint(coordinator)` helper starts its capture as `null` and requires it
  set (an empty capture used to pass `ShouldNotContain`), and
  `Recovery_WriterWithoutAbortRecord_…` now asserts which abort records the hook rejected.
- **Transactions.Tests, P2**:
  - the three Storage-and-record-space doubles are split (C9);
  - `BlockingIndex` (`TransactionCoordinatorRollbackTests.cs:782`) and the two `FailingIndex`
    doubles (`:819`, `RecordSpaceVersionStoreTests.cs:216`) derive from `RecordVersionIndex`.
  - **As landed:** five doubles were both a `Storage` and a record space, not three: the two
    crash suites' `CrashStorage` (`TransactionCoordinatorCheckpointCrashTests.cs`,
    `TransactionCoordinatorScrubCrashTests.cs`) joined `CoordinatorStorage`, `RollbackStorage`
    and `RecordStorage` after the plan was written. Each keeps its record helpers, now internal,
    and nests a `RecordSpace : TransactionRecordSpace` adapter over them, exposed as `Records`;
    the 55 coordinator constructions pass the storage's `Records`. The three index doubles override the
    protected cores, and `ControlledVersionStore` (`TransactionManagerRollbackTests.cs`) derives
    from `VersionStore` through the existing grant, whose comment now lists it.
- **Storage sub-components (rows 31, 34, 37, 38), P2.** No test names their interfaces. Their
  consumers reach them through `var` and member access, and compile unchanged against the sealed
  types.
- **Authenticator doubles** derive from `DatabaseAuthenticator` (P2). They are in KeyValuePair
  (`TestObjects/RejectingAuthenticator.cs:12`), Sql (`TestObjects/RejectingAuthenticator.cs:12`),
  Blob (`BlobDatabaseServerTests.cs:381`) and KeyValuePair.Client (`KeyValueClientTests.cs:202`).
  **Landed at P2:** each overrides `AuthenticateCoreAsync`; Blob's `RejectAuthenticator` drops
  its own cancellation check, which the base now makes. The frame-endpoint parameters of the
  client exchange doubles (P5, below) and of the Sql, KeyValuePair and Graph test protocol
  clients and exchanges are retyped to the bases in P2; the doubles still implement the P5
  exchange interfaces.
- **Client exchange doubles** derive from the exchange bases (P5):
  - Database.Client `StreamingClientTestHarness.cs:184` and `:254`;
  - `DatabaseProtocolExchangeTests.cs:58` and `:82`;
  - `DatabaseExchangeHealthTests.cs:85` and `:107`;
  - Graph `TestObjects/GraphCatalogTestExchange.cs:23`.
- **Observer doubles** (`RecordingObserver` in Sql.Client.Tests and KeyValuePair.Client.Tests)
  derive from the observer bases (P5).
- **Model builder-test doubles** derive in their model PR (P4), because typed `AddWorker` and
  `AddServer` need the bases. They are `RecordingWorker` and `RecordingServer` in the Blob,
  Documents and Graph `*ApplicationBuilderTests.cs` (`:232`/`:259`, `:234`/`:263`, `:234`/`:263`),
  and Sql's `SqlEngineCompositionTests.cs` doubles `ProbeWorker` (`:157`), `ProbeServer` (`:174`)
  and `CancellationFailureWorker` (`:228`).
  - **KeyValuePair at P4:** the model had no worker or server double (its builder tests composed
    only the real `KeyValueDatabaseServer`), so it gains `TestObjects/RecordingWorker.cs` and
    `TestObjects/RecordingServer.cs`, derived from the bases through their protected constructors
    with no grant (the server carries a private `IDatabaseServerContext` for the `Context` bridge
    until P6), and `KeyValueEngineCompositionTests` (§6.5). `KeyValueWorkerResilienceTests`'
    `EscapingWorker` implemented only `IDatabaseEngineWorker`; the typed `AddWorker` takes the base,
    so it derives from `DatabaseEngineWorker` and its first pass throws (row 7). The strategy
    doubles derive from the internal `KeyValueStorageStrategy` through the existing
    KeyValuePair → KeyValuePair.Tests grant (row 75); no grant was added.
  - **Graph at P4:** `GraphApplicationBuilderTests`' nested `RecordingWorker` and `RecordingServer`
    (and the nested `RecordingServerContext` P6 would have deleted) gave way to
    `TestObjects/RecordingWorker.cs` and `TestObjects/RecordingServer.cs`, KeyValuePair's doubles
    with the worker also recording the name of its pump thread; the server keeps a private
    `IDatabaseServerContext` for the bridge until P6. `GraphWorkerResilienceTests`' `EscapingWorker`
    derives from `DatabaseEngineWorker` (row 7). The fault-injecting strategy and the builder tests'
    recording strategy derive from the internal `GraphStorageStrategy` through the existing
    Graph → Graph.Tests grant (row 68); no grant was added, and Graph.Catalog and Graph.Storage
    needed none (rows 69 and 70).
  - **Documents at P4:** as Graph: `DocumentApplicationBuilderTests`' nested `RecordingWorker`,
    `RecordingServer` and `RecordingServerContext` gave way to `TestObjects/RecordingWorker.cs` and
    `TestObjects/RecordingServer.cs`, Graph's doubles with the model's names (the server keeps a
    private `IDatabaseServerContext` for the bridge until P6), and `DocumentWorkerResilienceTests`'
    `EscapingWorker` derives from `DatabaseEngineWorker` (row 7). The fault-injecting strategy and
    the builder tests' recording strategy derive from the internal `DocumentStorageStrategy`
    through the existing Documents → Documents.Tests grant (row 63); no grant was added, and
    Documents.Catalog needed none (row 64).
  - **Blob at P4:** as Documents: `BlobApplicationBuilderTests`' nested `RecordingWorker`,
    `RecordingServer` and `RecordingServerContext` gave way to `TestObjects/RecordingWorker.cs` and
    `TestObjects/RecordingServer.cs`, Documents' doubles with the model's names (the server keeps a
    private `IDatabaseServerContext` for the bridge until P6), and `BlobWorkerResilienceTests`'
    `EscapingWorker` derives from `DatabaseEngineWorker` (row 7). The fault-injecting strategy and
    the builder tests' recording strategy derive from the internal `BlobStorageStrategy` through the
    existing Blob → Blob.Tests grant (row 56); no grant was added, and Blob.Catalog, Blob.Storage
    and Blob.Client needed none (row 57). The server suite gained two private doubles of its own,
    a `FailingWorker` derived from the worker base (to fault the engine) and a listener that counts
    its binds and disposals (row 9).
  - **Sql at P4:** the old `SqlEngineCompositionTests`' nested `ProbeWorker`, `ProbeServer`,
    `ProbeContext` and `CancellationFailureWorker` gave way to `TestObjects/RecordingWorker.cs` and
    `TestObjects/RecordingServer.cs`, Blob's doubles with the model's names (the server keeps a
    private `IDatabaseServerContext` for the bridge until P6), plus a nested `YieldingServer` and
    `CancellationFailureWorker` derived from the bases. `SqlWorkerResilienceTests`' `EscapingWorker` derives from
    `DatabaseEngineWorker` (row 7), and `SqlExpressionDepthExecutionTests`' `ExternalEngineBuilder`
    is deleted (row 83). The strategy doubles derive from the internal `SqlStorageStrategy` through
    the existing Sql → Sql.Tests grant (row 84); no grant was added. Sql.Schema gained its grant
    (above); Sql.Catalog, Sql.Storage, Sql.Language and Sql.Client needed none. The worker release
    hook (row 7) reached every model's `RecordingWorker` (KeyValuePair, Graph, Documents, Blob,
    Sql) and the root suite's: each counts its releases in `DisposeAsyncCore` instead of
    implementing `IDisposable`.
- **Hosting.Tests, rewritten at P4 (Sql).** The project compiled linked copies of Sql.Tests'
  `FaultInjectingJournalSqlStorageStrategy` and Storage.Tests' journal-frame and device-fault
  helpers to fault a real SQL engine. A linked copy is compiled into Hosting.Tests, where the
  internal `SqlStorageStrategy` is out of reach, so the links are removed. Only the offline paths
  lost a real engine: no public API takes a real database offline, so
  `DatabaseWorkerHealthTests.CheckAsync_OfflineDatabase_ShouldBeUnhealthyUntilTheReopen` and
  `ResourceControlPlaneHostingTests.CheckAsync_WithDatabaseOfflineAfterFailedFsync_ShouldReportUnhealthyUntilReopened`
  report the offline database through Hosting's `RecordingEngine` double, and the real engine's
  offline path is the SQL model's own suites' (`SqlWorkerResilienceTests`,
  `SqlStorageOperationsTests`). The worker-fault path still runs on a real SQL engine:
  `CheckAsync_WorkerFaultsOnARealEngine_ShouldFollowTheWorker` registers a guided worker through
  the public typed `SqlDatabaseEngineBuilder.AddWorker`, fails a pass the test runs, and reads
  the real engine's `Faulted` and `Running` from the root base's fold and the application's
  degraded and healthy contributions from it. As first landed the whole test drove the double,
  through a `Report(EngineState)` setter the review deleted, on the reading that a real fault
  needed a public fault seam on a model; the public typed `AddWorker` is that seam for a worker
  fault. `CheckAsync_RegisteredWorkerLoopFailed_ShouldSayTheEngineStaysFaultedUntilDisposed`
  keeps the double: since phase 4 no engine of a model can register a worker whose loop escapes.
  `DatabaseCompositionTests` composes the real `SqlDatabaseServer` over an in-memory listener
  where it cast to the interface.
- **Hosting and Embedded doubles** derive from the root bases (P6). They are:
  - `RecordingEngine.cs:12`, `ProvisioningEngine.cs:8` and `ProvisioningDatabase` (`:101`);
  - `RecordingServer.cs:12` and `RecordingEngineWorker.cs:6`;
  - `DatabaseApplicationTests.cs:204`, `DatabaseCompositionTests.cs:391` and
    `ResourceControlPlaneHostingTests.cs:375`;
  - `EmbeddedDatabaseTests.cs:74`.
  - **At P3:** unchanged, as scheduled: Hosting and Embedded still compose engines through the
    interfaces the bases keep implementing, so their suites run unchanged and are P3's evidence
    that the bridge holds. None of them derived from `DatabaseEngineWorker`
    (`RecordingEngineWorker` implements the interface), so the worker's constructor change did not
    reach them either.
- **The base suite's own doubles (P3).** Database.Tests derives from the bases directly through
  their protected constructors, with no grant: `TestEngine`, `TestDatabase` (and
  `UnprovisionedTestDatabase`), `TestSession`, `TestTransaction`, `TestServer`,
  `TestServerSession` and `RecordingWorker` implement only the protected cores over in-memory
  state. The existing `ScriptedWorker` passes its name, kind and interval to the worker's new
  constructor.
- **Test-double contexts** go in P6 (Graph's, Documents' and Blob's became the private context of their
  `RecordingServer` at P4, above): `RecordingServerContext` in the Blob (`:277`), Documents
  (`:282`), Graph (`:282`) and Hosting (`RecordingServer.cs:57`) tests, and Sql's `ProbeContext`
  (`:205`; at P4 the private context of Sql's `RecordingServer`).

`RecordingBuilder` and `RecordingApplicationBuilder` implement the kept
`IDatabaseApplicationBuilder` and stay.

## 7. Phases and gates

Each arrow below reads "depends on", following the house convention. The prose in this section
states the same order.

```mermaid
flowchart LR
    P0["P0 rules and plan (#1256)"]
    J["#1236 journal fix"]
    P1["P1 dead and internal-only seams (#1257)"]
    P2["P2 kernel child roots (#1258)"]
    P3["P3 root bridge bases (#1259)"]
    P40["P4.0 generic builder state (#1260)"]
    P4["P4 one PR per model (#1260)"]
    P5["P5 clients (#1261)"]
    P6["P6 composition retype, root interfaces removed (#1262)"]
    P7["P7 ApplicationModel, templates, Studio, examples (#1263)"]
    P8["P8 per-row result types and docs (#1264)"]
    P1 --> P0
    P2 --> P1
    P2 --> J
    P3 --> P2
    P40 --> P3
    P4 --> P40
    P5 --> P4
    P6 --> P5
    P7 --> P6
    P8 --> P7
```

The order is linear apart from one outside prerequisite: phase 2 waits for #1236 as well as phase
1. The phases are serial for three reasons:

- the clients reference their model projects (`Sql.Client → Sql`, and so on);
- P6 cannot remove a root interface until every model and client stops naming it;
- the model PRs share `Database/shared/` and Studio, so they run one at a time.

**Every phase's gate:**

- `dotnet build` and `dotnet test` for every `resources/Database` project, passing the `.csproj`
  path, with no new warnings;
- COHRES001, COHRES002 and COHRES004 green;
- `pwsh build/scripts/Update-CohesionDependencyGraph.ps1 -Check`;
- the deviation line from `database-area.md` in the PR summary;
- each touched project's `docs/DESIGN.md` updated in the same commit;
- NativeAOT and trimming safety unchanged.

Each phase adds its own gate below.

**P0, #1256: rules and plan of record (this change).** Adds `database-area.md`, the pointers in
`general-rules.md` and `checklist.md`, owner decision O34a, and this file. No code changes, and no
per-project DESIGN changes. *Gate:* docs only. The dependency graph check passes, and the Database
root builds with 0 warnings, as before.

**P1, #1257: delete dead and internal-only seams.** Rows 14 to 18, 26, 28 to 30, 32, 35, 42, 43,
46 and 47 (row 43 per C1). Rows 31, 34, 37 and 38 moved to P2 (§5.1).

- **Projects deleted (D6).** `Database.Governance`, `Database.Replication` and
  `Database.Sql.Replication`, plus the four empty model shells `Blob.Replication`,
  `Documents.Replication`, `Graph.Replication` and `KeyValuePair.Replication`, which hold only
  csprojs.
- **Touch points for those deletions:**
  - `CohesionPackaging.psm1:208` and `:462-463`;
  - `Database.Runtime/Directory.Build.props:38`, and the exclusion comment at `:98-113`;
  - the root `Assimalign.Cohesion.Database.csproj:16`;
  - `resource-database.yml:31-34` and `:50`, and `sdk-smoke.yml:27`, `:76`, `:322` and `:392`;
  - the four `.slnx` files: the root `Assimalign.Cohesion.slnx`,
    `resources/Assimalign.Cohesion.Resources.slnx`, the area's
    `resources/Database/Assimalign.Cohesion.Database.slnx`, and
    `resources/Database/Assimalign.Cohesion.Database/Assimalign.Cohesion.Database.slnx:23`;
  - the Governance child-root lists in `.claude/rules/resource-areas.md:92` and `:289`
    (done at the P1 merge, under the owner's approval of the deletion);
  - `resources/Database/README.md:11`, `:100`, `:111` and `:143-144`;
  - `docs/resources/Database/DESIGN.md:43`, `:72`, `:83`, `:90`, `:94`, `:96`, `:120`, `:122`
    and `:130`. **Corrected at P1:** the two dated decision-log rows, "`Sql.Replication` missing"
    (`:469`) and "Child roots roll up under the root" (`:495`, which lists `Governance`), are
    history and stay as written (now `:473` and `:499`), as the dated rows of
    `DATABASE_PROGRAM_PLAN.md` do;
  - `Database/docs/OVERVIEW.md:19` and `:69`, `Database/docs/DESIGN.md:14`, and
    `Database.Storage/docs/DESIGN.md:646`;
  - `docs/programs/DATABASE_PROGRAM_PLAN.md` (five mentions; at P1 only the lane table is current
    text, and the dated decision-log rows stay as history);
  - the comment in `build/Targets/Build.Rules.targets` that lists the child roots;
  - re-run `rg -n "Governance|Replication" --glob '*.md' --glob '*.slnx'` before the commit,
    because these lines move.
- **Execution.** `QueryStatementResult` goes with the pipeline (row 15), and
  `Database.Execution/docs/OVERVIEW.md:13` with it. **At P1** `QueryExecutionException` goes too
  (row 15), `SqlQueryExecutor` loses the throwing overload that satisfied `IQueryExecutor`
  (row 14), and the Execution test project keeps a two-test `QueryRequestTests` suite.
- **Storage.** `Storage.BufferPool` becomes internal (row 30). The `Storage` and `StorageJournal`
  deviation markers are written (§8). The members that implemented the interfaces with
  `<inheritdoc />` carry the interfaces' documentation themselves.
- **Transactions.** `TransactionManager`, `TransactionLog` and `TransactionRecovery` per §5.2, and
  the coordinator hooks that replace the `IStorage` and `IStorageJournal` interception (§6.9). The
  Transactions → Transactions.Tests grant already existed at P1; its comment in
  `src/Properties/AssemblyInfo.cs` lists every use P1 added. The MVCC binding paragraph of
  `docs/resources/Database/DESIGN.md` §3.8 (`:352`) names `TransactionManager`, the internal
  journal-bound log and `Storage.ReserveTransactionSequence`/`BeginTransaction(long)` instead of
  the deleted interfaces (found by the P1 review).
- **Re-verified at P1 against the code after #1251 to #1253 (and #1242, #1254, #1268).** Every
  deleted interface still had no consumer outside the rows above; three public carriers the
  review had missed were retyped (§5.2: `BTreeIndexManagerOptions.Storage`, the five
  `WriteAheadJournal` properties, the coordinator's constructor and `Manager`). The coordinator
  hooks were still needed (§6.9): #1252's stream-level triggers replaced the one test that failed
  a journal write, but not the checkpoint, sequence and abort-record interceptions.
- **Outside the rows.** P1 also removed a duplicate `using Assimalign.Cohesion.ApplicationModel;`
  (a pre-existing CS0105 warning) from `Database.Testing`'s `DatabaseSampleHostEndToEndTests.cs`.

*Gate:*

- every Database suite, Indexing.Tests and Sql.Tests by name;
- the coordinator tests of §6.9 by name (eight tests at P1), each asserting that its hook fired;
- `Assert-CohesionReleaseInventory`;
- `dotnet pack resources/Database/Assimalign.Cohesion.Database.Runtime/src/Assimalign.Cohesion.Database.Runtime.csproj`,
  because the framework loses Governance;
- the dependency graph regenerated in the same commit.

P1 and #1236 both touch `Storage.cs`, at different lines; whichever lands second rebases.

**P2, #1258: kernel child roots.** It waits for #1236. There is one commit per child root, in
dependency order, and each updates its consumers mechanically:

1. **Storage:** rows 31, 33, 34, 36, 37 and 38, `Storage.Model`, and the `StorageJournal` audit
   (§5.3).
2. **Transactions:** rows 39 to 41, 44 and 45; §6.1, §6.2 and C9; `TransactionManager.Create`
   retyped.
3. **Indexing:** rows 19 to 23, and §6.3.
4. **Protocol:** rows 24 and 25, and the `ProtocolFraming` removal.
5. **Security:** row 27.

**Protocol and Security, re-verified and landed at P2 (wire track).** Rows 24, 25 and 27 held
against the code after #1251 to #1253 and P1, with two corrections: `IProtocolFrameWriter` also
had `FlushAsync` (row 25), and the public carriers the rows did not name are retyped with them
(`ProtocolChannel.Reader`/`Writer`, the Database.Client exchange interfaces' frame parameters,
`BlobProtocolTransfer`, the four servers' `Authenticator` option). Neither project had a §5.3
audit. The Protocol and Security `Abstractions/` folders are gone with their last interfaces
(rule 11). No caller in Hosting, Embedded, Testing, Studio or the Sdk named these types. The
Protocol, Database.Client, every model client and server suite, Hosting and Embedded run
unchanged; Database.Security gains its first test project (`DatabaseAuthenticatorTests`, five
tests) for the base's argument and cancellation checks.

Row 27 is the track's one behaviour change, pending owner confirmation at merge: an
application authenticator's core no longer sees a null database or principal
(`ArgumentNullException`) or an already-canceled token (`OperationCanceledException`). No
shipped caller can observe it: the four servers pass strings decoded by
`ProtocolPayload.ReadString`, which never returns null, and `AllowAll` already threw on a
canceled token. Row 25 kept the payload bound in the stream leaf, not in the base's public
member, until the owner moved it into the base on 2026-10-06 (decision 29, landed; row 25).
P8's cohesion-docs sweep picks up the deleted names in
`docs/dotnet-apis/resources/database/`: `assimalign-cohesion-database-client/examples/database-protocol-exchange-tests.md`,
`assimalign-cohesion-database-keyvaluepair-client/examples/key-value-client-tests.md`,
`assimalign-cohesion-database-security/` (`index.md`, `design.md`),
`assimalign-cohesion-database-protocol/` (`index.md`, `design.md`) and
`assimalign-cohesion-database-sql/design.md`.

*Gate:* the crash and durability suites (`CrashRecoveryTests`, the crash-capture and
fault-injection strategies), and #1226's lock-retention tests. An optional NativeAOT
microbenchmark of the insert path runs before and after.

**Kernel track (Storage, Transactions, Indexing), as landed.** Each row was re-verified against
the code after #1251 to #1254, #1268 and P1 before it changed; where the code had moved, the row
says what landed.

- **Storage.** Rows 31, 33, 34, 36, 37 and 38 landed as sealed public types moved out of
  `Internal/`, and `Abstractions/` is gone from the project. Two departures from the rows: the page
  manager is no longer disposable (row 34), and the unit iterator's `Next` reports the default unit
  instead of `null` (row 38). `Storage.Model` is a constructor-set field (§5.3), and the
  `StorageJournal` audit collapsed the journal into one sealed type with the former leaf's public
  constructors (§5.3, §8). No behavior changed: Storage.Tests (293), Transactions.Tests (101) and
  Indexing.Tests (75) pass unchanged apart from the retypes and the dropped page-manager `using`
  declarations.
- **Transactions.** Rows 39 to 41, 44 and 45 landed, and the project's `Abstractions/` is gone:
  `TransactionContext` and `LockManager` are sealed (promoted from `Internal/`), and
  `TransactionRecordSpace`, `RecordVersionIndex` (both `protected`) and `VersionStore`
  (`private protected`) are NVI bases with deviation markers. §6.1 and §6.2 landed, §6.2 with the
  #1268 wait abandonment the plan predates; C9 split five doubles, not three (§6.9);
  `TransactionManager.Create` is retyped. Indexing's adapter became `BTreeRecordVersionIndex` in
  this commit (row 40). The engines changed only by retype, apart from the five record-space
  adapters and two index-undo adapters, which override the protected cores, and the three
  statement decorators, which became `PinStatementSnapshot` calls.
- **Indexing.** Rows 19 to 23 and §6.3 landed, and the project's `Abstractions/` is gone:
  `BTreeIndex`, `BTreeCursor` and one `BTreeIndexManager` for manager and registry are sealed,
  and `BTreeIndexManagerOptions.TransactionSource` is the per-engine delegate. The four engine
  wrappers became private `ResolveStatementBracket` methods with their messages unchanged.
- **Gate, as run.** Every Database suite passes with the baseline counts after each of the three
  commits (Storage.Tests 293, Transactions.Tests 101, Indexing.Tests 75, Sql.Tests 1087 and the
  rest as listed in the phase brief), including the crash and fault-injection suites
  (`CrashRecoveryTests`, the coordinator checkpoint and scrub crash tests, each model's crash
  doubles) and the #1226 lock-retention and #1268 abandon tests. One run of Sql.Tests after the
  Indexing commit failed the timing guard
  `Delete_WideFanOutCascade_ShouldTakeTimeLinearInChildren` (a per-child cost ratio) while other
  builds loaded the machine; it passed three isolated reruns and a full rerun (1087). No
  NativeAOT microbenchmark was run.
- **Review fixes.** The statement view forwards its end claim and apply admission to the
  transaction's own context, and a new `TransactionStatementViewTests` suite covers §6.1
  directly (Transactions.Tests 101 → 108). Recovery reads the sealed journal sequentially with no
  dead type test, and nine identity casts left in Storage.Tests by the sealing are gone. Stale
  text was fixed: the Storage OVERVIEW snippet (a constructor-set model, not an override), the
  journal's note on its former leaf, "an `TransactionContext`" at six sites, and the lane table of
  `DATABASE_PROGRAM_PLAN.md`, which still named `ITransactionContext`.
- **Left for later.** The marker sentence of `database-area.md` and the header Status line were
  updated at the P2 merge. Whether `StorageJournal` and `BTreeRecordVersionIndex` keep public
  constructors was an owner question (§5.3, row 40); the owner chose `Create` factories on
  2026-10-06 (decision 27), landed with the `Storage.CommitDurability` validation (decision 26,
  Storage `DESIGN.md`, "Changing the setting"). The kernel review of decision 26 fixed `None` at
  initialization in both directions: a switch into it while a logical commit waited in the gate
  let a non-durable checkpoint's truncation become durable through the waiter's own fsync, which
  lost that acknowledged commit and an earlier one to a power loss. It also moved the
  group-commit window bound to engine `Create` in all five engines.

**P3, #1259: root bridge bases.** Rows 1, 5, 9, 11, 12 and 13 add `DatabaseEngine`,
`DatabaseInstance`, `DatabaseSession`, `DatabaseTransaction`, `DatabaseServer` and
`DatabaseServerSession`. Each still **explicitly implements** its old root interface, so
Hosting and Embedded keep compiling. That is today's guided-abstract-base rule. No model leaf
derives from them yet; that happens per model in P4. P3 also:

- writes the consolidated state machine and the one "already active" check into the bases
  (§6.4), and the Database.Tests suite that drives them through root-level doubles;
- writes the attach semantics and the base fields that typed `new` properties sit on (§6.5);
- folds schema provisioning into `DatabaseInstance` (row 8) and audits `CompiledSchema`;
- converts `DatabaseEngineWorker` to NVI (row 7). Its 22 worker leaves already derive from it, so
  they change in P3.

`DatabaseServer` keeps `IDatabaseServer.Context` as a temporary abstract member until P6.
*Gate:* the new Database.Tests base suite (end gate, Faulted state, repeatable rollback,
cancellation before an end, `Abort`, the one message, attach after freeze refused); every
existing Database suite unchanged and green; the engine-worker suites of every model.

**P3, as landed (re-verified 2026-10-05 against the code after P1 and P2).** Two commits: the
worker's NVI conversion, then the six bases; the review's fixes followed in a third (below). Every
row was re-read against the code first; where
the code had moved, the row now says what landed:

- **Rows 1, 5, 9, 12 and 13** landed as planned, with the members the interfaces had gained since
  the plan: `OfflineDatabases` (#1243) is the engine's one abstract public member, and
  `DatabaseInstance` owns idempotent disposal because `IDatabase` is disposable (rows 1 and 5).
- **Row 11 was corrected:** a server session's version and principal are not known when it is
  constructed, so they are protected one-shot setters, not constructor parameters.
- **§6.4** landed with two additions the code required: `Abort` is `AbortAsync`, joined by the
  teardown's `CloseAsync`, and the commit's refusal while work runs is one admission
  (`TryBeginOperation`/`EndOperation`) under the base's lock. The messages and the other behavior
  that change when a model adopts the bases are listed in §6.4 for each P4 PR.
- **Row 7 and §5.3:** the worker's name, kind and interval are constructor-set; the 22 leaves
  changed with it. The engines' pump and state fold are in `DatabaseEngine`; the shared copy stays
  compiled into each model until its P4 PR, because no model engine derives from the base yet.
  `CompiledSchema` was audited and left unchanged.
- **Row 8:** `SupportsSchemaProvisioning` and `ApplySchemaAsync` landed as planned.
- **No caller changed.** Hosting, Embedded, Testing, Studio, the Sdk and every model still compose
  through the interfaces the bases implement; only the worker leaves and the root suite's
  `ScriptedWorker` moved to the worker's new constructor. The Hosting and Embedded doubles stay
  for P6 (§6.9).
- **Gate, as run.** Every Database project, Sdk.Database and Studio build with no new warning
  (CS2008 on Database.Refs predates P3). Database.Tests grows from 27 to 101 tests: the base
  suite (`DatabaseTransactionTests`, `DatabaseSessionTests`, `DatabaseEngineTests`,
  `DatabaseInstanceTests`, `DatabaseServerTests`) plus two worker-constructor tests. Every other
  Database suite passes with its baseline count, among them the engine-worker and resilience
  suites of every model (Sql 1087, KeyValuePair 158, Graph 360, Documents 152, Blob 129) and
  Hosting (53) and Embedded (4), which exercise the workers through their engines. The Studio
  `--smoke` run matches its baseline, and the dependency graph check passes (no reference
  changed). Database.Testing's suite and the SampleHost fixture need a local SDK pack and were
  not run.
- **Review fixes (a third commit).** Three reviews approved the bases with documentation and
  rule-conformance findings; the code changes are small and change no model:
  - `DatabaseSession.DisposeAsync` always reports a failure in one `AggregateException` ("The
    session failed to close."), as `DatabaseEngine` and the Graph, Documents and Blob sessions
    do; it had rethrown a single failure as itself.
  - `DatabaseEngine.GetDatabasesAsync` takes the cancellation fast path every other NVI member
    takes (rule 4), and the base suite pins it.
  - `DatabaseTransaction.AbortAsync` is `protected`, not `protected internal` (§6.4).
  - The `DatabaseServer` lifecycle constants and the base suite's private fields follow the
    naming rules, and its using directives the ordering rule (`general-rules.md`); exception
    docs on `RollbackAsync` and `DisposeAsync`, the never-null contract on `Sessions` (rule 9),
    and the worker's class summary were completed.
  - The plan and the root DESIGN now list every behavior a model's P4 PR changes, not only the
    messages: disposal-failure shapes, BEGIN's refusal order, the engine's guards and blank-name
    rejection, Blob's server start refusal (row 9, which had said all four servers shared the
    lifecycle) and three more SQL changes (§6.4). The worker-disposal type test is scheduled for
    the P4 PR that deletes the shared pump (row 7). The area record
    `docs/resources/Database/DESIGN.md` names the bases.
  - Gate, rerun after the fixes: a no-incremental rebuild of the Database solution (Testing's
    tests left out) has no Database warning but CS2008 on Database.Refs; every suite keeps its
    count (Database.Tests 101, the guard assertion joining an existing test), Sdk.Database 18,
    Studio builds clean and its `--smoke` run gives 83 passed, 0 failed, 1 skipped, and the
    dependency graph check passes.
- **Owner questions at the P3 merge**, beside the three P2 questions above, which P3 left
  untouched:
  1. **Rule 6 versus row 11.** Rule 6 of `database-area.md` lists "protocol version, principal"
     among the values fixed at construction, and `DatabaseServerSession` sets them once through
     protected, non-virtual setters, because a server session exists from accept, before its
     handshake. The rule file is binding and changes only by owner decision (O34a), so P3 did not
     edit it. Proposed wording, on approval: drop "protocol version, principal" from the list and
     add "A value a leaf learns only after construction (a server session's negotiated protocol
     version and authenticated principal) is a base field behind a non-virtual getter, set once
     through a protected, non-virtual method that throws `InvalidOperationException` on a second
     call." Still open at the Blob PR, which uses the setters as they are (Blob decision 30, below).
  2. **Blob server start retry (row 9).** Under the base a start refused because the engine is
     not `Running` is terminal. Keeping Blob's retry needs a non-terminal refusal path in the
     base; the plan assumes the terminal shape unless the owner asks for the retry. The Blob PR
     landed the terminal shape (Blob decision 31, below).

**P4, #1260: one PR per model.**

- **P4.0, its own PR.** Make `Database/shared/DatabaseEngineBuilderState.cs` generic over the
  engine (`DatabaseEngineBuilderState<TEngine>`). Today it is typed
  `Func<IDatabaseEngine, IDatabaseEngineWorker/IDatabaseServer>` (`:12-13`). Untyped builders
  adapt through a lambda until their model PR lands, and P6 tightens the constraint to
  `DatabaseEngine`.
  - **As landed (re-verified 2026-10-05 against the code after P3).** Two corrections, both forced
    by a P4 period in which adopted and unadopted models compile the same shared file (§6.5,
    row 6):
    - The state is `DatabaseEngineBuilderState<TEngine, TWorker, TServer>`. An unadopted model's
      products are the root interfaces and an adopted one's are the bases, so the product types
      are parameters until P6 fixes them to the bases.
    - `Complete` takes the leaf's compose method and makes no attach check, but keeps the
      ownership test as the guard on disposing a refused product. A bridge overload adapts the two
      attach callbacks into a compose method and makes the checks the base makes.

    The five builders changed only the state's type arguments. Their factories reach the typed
    state through delegate variance, and their `Build()` calls the bridge. No behavior changed:
    the messages, the order of factories and attaches, and what is disposed on each failure are
    the ones the state had. *Gate, as run:* every Database project, Sdk.Database and Studio build
    with no new warning (CS2008 on Database.Refs predates P4.0); every Database suite keeps its
    baseline count (Database.Tests 101, Sql 1087, KeyValuePair 158, Graph 360, Documents 152,
    Blob 129, Hosting 53, Embedded 4, Sdk.Database 18 and the rest unchanged), among them each
    model's builder and composition tests (repeated product, foreign server, null product, failing
    factory, the premature-build compensation); the Studio `--smoke` run gives 83 passed, 0
    failed, 1 skipped, as before; the dependency graph check passes (no reference changed); and the
    Database runtime producer packs. The scratch probe of §6.5 ran the adopted path.
  - **Review (2026-10-05).** Two reviewers approved with minor findings only, applied on top of
    the KeyValuePair PR's review commit, the first in-repository user of the compose overload:
    the state enforces the compose method's contract instead of trusting it (§6.5, "The contract,
    enforced"); the interleaving and a refused product's failed cleanup are pinned by tests on the
    bridge (SQL) and the compose method (KeyValuePair), with the contract cases driven through the
    state directly (§6.5, "In-repository evidence"); and the `<exception>` docs of both `Complete`
    overloads name what each really throws. The five builders did not change: a correct compose
    method, the bridge's included, never reaches a check. *Gate, as run:* the five model
    assemblies rebuild from scratch with no warning, and no Database project warns; every Database
    suite passes, with only the two that gained tests changing count: Sql 1087 to 1089 and
    KeyValuePair 179 to 186. The rest: Database.Tests 101, Language 105, Types 93, Storage 293,
    Transactions 108, Indexing 75, Execution 2, Protocol 21, Security 5, Sql.Language 999,
    Sql.Catalog 47, Sql.Schema 38, Sql.Storage 14, Sql.Client 314, Documents 152,
    Documents.Language 288, Documents.Catalog 8, Documents.Storage 29, Graph 360, Graph.Language
    387, Graph.Catalog 19, Graph.Storage 17, Graph.Client 57, Blob 129, Blob.Catalog 5,
    Blob.Storage 13, Blob.Client 21, KeyValuePair.Catalog 4, KeyValuePair.Storage 3,
    KeyValuePair.Client 10, Client 41, Hosting 53, Embedded 4, ApplicationModel 15 and
    Sdk.Database 18. Studio, the dependency graph and the runtime producer pack were not rerun:
    the change is internal to the shared state and two test files, so no reference, public member
    or Studio-visible type changed.
- **Then KeyValuePair, Graph, Documents, Blob and Sql, one PR each, serialized.**
  - The leaves derive from the bridge bases and become public sealed.
  - The model deletes its own copy of the explicit-transaction state machine and the "already
    active" check, adopts the base's, adds its typed `new` fields, and updates its message and
    behavior assertions (§6.4). Sql's transaction gains the end gate.
  - The model's engine stops compiling `shared/DatabaseEngineWorkerPump.cs`; the last model PR
    deletes it and gives the worker base its disposal hook (row 7).
  - The `Add<Model>` composition verb is retyped to the sealed builder (§5.2).
  - The model children collapse into sealed types, and the builder becomes sealed with typed
    `AddServer` and `AddWorker`. The builder composes through its leaf's `Compose` instead of the
    bridge, and the state's type arguments move to the bases (§6.5); the last model PR deletes
    the bridge overload.
  - The strategies become internal abstract (D9), and the model's `Abstractions/` folder goes.
  - Promoted types leave `Internal/` and change namespace.
  - The model's tests, fixtures (including the Documents recovery fixture's casts) and Studio
    workspace are updated. Studio, which is MAUI with `IsPackable=false`, is built in every model
    PR.
- **KeyValuePair, as landed (re-verified 2026-10-05 against the code after P4.0).** The landing
  commit on `refactor/L03.02.01.56.05-concrete-types-p4-kv` and its review commit on
  `refactor/L03.02.01.56.05-concrete-types-p4-kv-review`. Rows 73 to 77 held against the code,
  and the leaves landed as the P4 bullets say, with these readings of the code:
  - *Leaves.* The engine, database, session, transaction and server are public sealed leaves of
    the bases; the database, session, transaction and builder left `Internal/` for the
    `RootNamespace`. The server session stays an internal sealed leaf (row 11), and the five
    workers stay internal sealed (they derived from `DatabaseEngineWorker` since P3, and rule 1
    keeps per-model plumbing internal). `KeyValueDatabaseServerContext` stays for the `Context`
    bridge until P6 (row 10).
  - *The shared pump.* Not compiled, through `COHESION_DATABASE_ENGINE_PUMP_IN_BASE` (row 7); the
    worker disposal hook stays with the last model PR, which deletes the file.
  - *The composition verb and the builder* (rows 6 and 74, §5.2): typed, and composing through
    `Compose` (§6.5). The plan's "the last model PR deletes the bridge overload" holds: four
    builders still use it.
  - *Children:* the catalog and its snapshot are sealed (rows 76 and 77), and the KeyValuePair and
    KeyValuePair.Catalog `Abstractions/` folders are gone, as is the catalog's `Internal/`.
    KeyValuePair.Storage had no interface row. The KeyValuePair.Client rows (78 to 80) are P5's,
    as the phase table says; this PR changed only the client's tests, for the server's new
    `Sessions` and the typed factories.
  - *Behavior.* §6.4's list, accounted for item by item there; §6.5's composition paths; the
    worker resilience suite's interface-only worker became a base worker (row 7, §6.9).
  - *Studio.* `KeyValueWorkspace` compiles unchanged: it reaches the engine and its sessions
    through `ModelWorkspace`'s root-typed members, whose retype is P7's, and the client's
    interfaces are P5's.
  - *Docs.* KeyValuePair `DESIGN.md` ("Concrete types", and the sections that named the
    interfaces) and `OVERVIEW.md`, KeyValuePair.Catalog's `DESIGN.md` and `OVERVIEW.md`, the root
    `DESIGN.md` ("Root bases"), the area record's model table and Indexing's resolver table.
  - *Review.* The typed `TryGetDatabase` overload (§6.5), which the landing had left root-typed on
    a false ambiguity claim, so the server session's cast and the tests' `ShouldBeOfType` are
    gone; assertions for §6.4's offline and token orderings, the offline teardown's `Faulted`
    state and the nested database-close aggregate, which the landing had left unasserted or
    called unobservable; the exception docs of the typed operations, the parameterless BEGIN and
    the catalog's two async writes; and four test names given their scenario segment.

  *Gate, as run:* a no-incremental build of every Database project but Database.Testing's tests
  and the SampleHost fixture (they need a local SDK pack) has no Database warning but CS2008 on
  Database.Refs; every Database suite passes with its baseline count (Database.Tests 101, Sql 1087,
  Sql.Language 999, Graph 360, Documents 152, Blob 129, Hosting 53, Embedded 4 and the rest as
  listed in the phase brief) apart from KeyValuePair.Tests, which grows from 158 to 179 (15
  composition tests, four session and transaction contract tests, two engine tests), among
  them the #1188, #1225 and #1226 suites in process (`KeyValueTransactionFailureTests`,
  `KeyValueLifecycleTests`, `KeyValueMvccTests`) and over the wire
  (`KeyValueTransactionFailureWireTests`, KeyValuePair.Client's
  `KeyValueTransactionFailureClientTests`); KeyValuePair.Catalog 4, KeyValuePair.Storage 3 and
  KeyValuePair.Client 10 keep theirs; Sdk.Database 18; Studio builds clean and its `--smoke` run
  gives 83 passed, 0 failed, 1 skipped; the dependency graph check passes (no reference changed);
  and the Database runtime producer packs.
- **Graph, as landed (re-verified 2026-10-05 against the code after the KeyValuePair PR).** Four
  commits on `refactor/L03.02.01.56.05-concrete-types-p4-graph`, based on the integration branch's
  `45c9fc49` (below): the child roots, the model, a test pin of the session's argument checks
  before the offline refusal, then the review's fixes (below).
  Rows 65 to 70 held against the code, with the readings their entries record, and the leaves
  landed as the P4 bullets say:
  - *Leaves.* The engine, database, session, transaction and server are public sealed leaves of
    the bases; the database (two partial files), session, transaction and builder left `Internal/`
    for the `RootNamespace`, and so did `GraphSchema`'s implementation. The server session stays an
    internal sealed leaf (row 11), the four workers stay internal sealed, and
    `GraphDatabaseServerContext` stays for the `Context` bridge until P6 (row 10).
  - *The shared pump.* Not compiled, through `COHESION_DATABASE_ENGINE_PUMP_IN_BASE` (row 7).
  - *The composition verb and the builder* (rows 6 and 66, §5.2): typed, and composing through
    `Compose` (§6.5); three builders still use the bridge.
  - *Children:* `GraphCatalog` and `GraphStore` are sealed (rows 69 and 70), in a commit of their
    own, and the Graph, Graph.Catalog and Graph.Storage `Abstractions/` folders are gone. The
    Graph.Client rows (71 and 72) are P5's; this PR changed only the client's tests (the harness's
    typed database, the server's `Sessions`).
  - *Behavior.* §6.4's Graph accounting, item by item, and §6.5's composition paths.
  - *Studio.* `GraphWorkspace` compiles unchanged: it reaches the engine and its sessions through
    `ModelWorkspace`'s root-typed members, whose retype is P7's, and the client's interfaces are
    P5's; only Studio's README named `IGraphSchema`.
  - *Fixture.* The recovery fixture's two `(IGraphDatabase)` casts are gone.
  - *Docs.* Graph `DESIGN.md` ("Concrete types", and the sections that named the interfaces),
    `OVERVIEW.md` and the API page, Graph.Catalog's and Graph.Storage's `DESIGN.md`, `OVERVIEW.md`
    and API pages, the root `DESIGN.md` ("Root bases"), the area record's model table, Indexing's
    resolver table and Studio's README.
  - *The base.* The merge that landed KeyValuePair (`0c61c710`) left the KeyValuePair test
    project uncompilable (CS0246: `e3e6787d`'s `FileBytes` helper took the internal
    `KeyValueDatabaseInstance` that `a8227b0d` renamed); the integration branch fixed it itself in
    `45c9fc49`, and this PR is based on that commit, so the gate ran on top of the fix.
  - *The review's fixes.* The `<exception>` documentation of the newly public surface is complete:
    `OperationCanceledException` and the retryable kernel outcomes on the six typed operations,
    every reachable exception on the `GraphSchema` operations (among them `GraphCatalogException`
    and the catalog's `ArgumentException`, which cross the model boundary untranslated, as before),
    `ObjectDisposedException` on both `BeginTransactionAsync` overloads, the argument and
    cancellation exceptions of `GraphStore`'s members, and the exceptions `GetDatabasesAsync`
    raises while it opens a database. `GraphStore.HasIndex` and `SearchIndexAsync` check a null
    snapshot themselves, which they did only once an index definition existed. A guard order the
    accounting had missed (a closed session of a dropped database) is accounted for in §6.4 and
    asserted, as are the one-statement hold and the `QueryRequest` seam's token check. Also
    corrected: `GraphStore`'s two private constants (camelCase), `GraphSchema`'s private
    `WriteAsync`, three test names, a leftover engine cast in `GraphStorageOperationsTests` and the
    §6.5 suite attribution. `Members_…` and `Pump_…` keep the names of the KeyValuePair tests they
    mirror.

  *Gate, as run:* a no-incremental build of every Database project but Database.Testing's tests,
  the SampleHost fixture (they need a local SDK pack) and the stray `Cache/src` test csproj the area
  solution does not list, plus Sdk.Database, has no Database warning but CS2008 on Database.Refs
  (the only other warnings are the DependencyInjection and Configuration libraries' own); every
  Database suite passes with its baseline count (Database.Tests 101, Language 105, Types 93,
  Storage 293, Transactions 108, Indexing 75, Execution 2, Protocol 21, Security 5, Sql 1089,
  Sql.Language 999, Sql.Catalog 47, Sql.Schema 38, Sql.Storage 14, Sql.Client 314, Documents 153,
  Documents.Language 288, Documents.Catalog 8, Documents.Storage 31, Graph.Language 387,
  Graph.Catalog 19, Graph.Storage 17, Graph.Client 57, Blob 130, Blob.Catalog 5, Blob.Storage 14,
  Blob.Client 21, KeyValuePair 186, KeyValuePair.Catalog 4, KeyValuePair.Storage 3,
  KeyValuePair.Client 10, Client 41, Hosting 53, Embedded 4, ApplicationModel 15, Sdk.Database 18)
  apart from Graph.Tests, which grows from 360 to 396 (21 composition tests, eleven session and
  transaction contract tests, two of them added by the review, three engine tests and one scope
  test), among them the #1188, #1225 and #1226 suites in process (`GraphTransactionFailureTests`,
  `GraphConcurrencyTests`, `GraphStorageOperationsTests`, `GraphWorkerResilienceTests`) and over
  the wire (`GraphServerProtocolTests`, Graph.Client's `GraphTransactionFailureWireTests`), and
  the #1139 and #1228 suites in process (`GqlLabelChainExecutionTests`, `GqlLabelDirectionExecutionTests`,
  `GqlParseStrictnessExecutionTests`, `GqlProfileExecutionTests`, `GqlUnknownTokenWarningTests`)
  and over the wire (`GraphLabelChainWireTests`, `GraphLabelDirectionWireTests`,
  `GraphServerProtocolTests`); Studio builds clean and its `--smoke` run gives 83 passed, 0
  failed, 1 skipped; the dependency graph check passes (no reference changed); and the Database
  runtime producer packs.
- **Documents, as landed (re-verified 2026-10-06 against the code after the Graph PR).** Two
  commits on `refactor/L03.02.01.56.05-concrete-types-p4-documents`, based on the integration
  branch's `4e07649c`: the child root, then the model; the review's fixes (below) followed in a
  third, on `refactor/L03.02.01.56.05-concrete-types-p4-documents-review`. Rows 60 to 64 held
  against the code, with the readings their entries record (row 61's and §6.6's line numbers had
  moved), and the leaves landed as the P4 bullets say:
  - *Leaves.* The engine, database, session and transaction are public sealed leaves of the bases;
    the database, session, transaction, collection and builder left `Internal/` for the
    `RootNamespace`. The four workers stay internal sealed. Documents has no wire server, so it has
    no server, server session or server context.
  - *The shared pump.* Not compiled, through `COHESION_DATABASE_ENGINE_PUMP_IN_BASE` (row 7).
  - *The composition verb and the builder* (rows 6 and 62, §5.2): typed, and composing through
    `Compose` (§6.5); two builders (Blob, Sql) still use the bridge.
  - *Children:* `DocumentCatalog` is sealed (row 64), in a commit of its own, and the Documents and
    Documents.Catalog `Abstractions/` folders are gone. Documents.Storage had no interface row and
    needed nothing (`DocumentStorage` was already a sealed `Storage` leaf); Documents.Language and
    Documents.Security declare no interface; Documents.Client's `src` is empty.
  - *Option B* (§6.6): the session runs the four session-bound collection operations, and
    `session.Database` is the unbound database; the view is deleted.
  - *Behavior.* §6.4's Documents accounting, item by item, and §6.5's composition paths.
  - *Studio.* `DocumentWorkspace` runs its collection tools on the session (option B) through a
    downcast from `ModelWorkspace`'s root-typed session, which P7's retype removes; `QueryPage`'s
    heading names `DocumentCollection`.
  - *Fixture.* The recovery fixture's two `(IDocumentDatabase)` casts are gone.
  - *Docs.* Documents `DESIGN.md` ("Concrete types", and the sections that named the interfaces or
    the session-bound view) and `OVERVIEW.md`, Documents.Catalog's `DESIGN.md` and `OVERVIEW.md`,
    Documents.Language's `DESIGN.md`, the root `DESIGN.md` ("Root bases"), the area record's model
    table and Indexing's resolver table.
  - *The review's fixes.* Three reviews approved with minor findings only. Option B's two
    consequences that had gone unrecorded are recorded (§6.4, §6.6, the session's, the database's
    and Documents `DESIGN.md`'s remarks) and asserted: disposing `session.Database` leaves the
    engine refusing to reopen the database (`ObjectDisposedException`), and a write through
    `session.Database` while the session's transaction has written waits for that transaction's
    writer lock until its token is canceled (a new test). The commit after a session closed on a
    transaction a failed statement had aborted is pinned beside the active case, as Graph pins
    both (`CommitAsync_AfterSessionClosed_ShouldReportWhyNothingCommitted`, now a theory). The
    session's class remarks no longer say a closed session refuses everything with one message
    (its collection operations and a collection's document operations check the database first);
    `DocumentCollection.PutAsync` documents the `DocumentCatalogException` an indexed scalar beyond
    the 1024-byte key limit raises, which crosses the model boundary untranslated, as before; the
    database's async disposal path is in §6.4's accounting. Also corrected: the using-directive
    groups of the files the PR touched (a blank line between the System and Cohesion groups, and
    the workers' usings above the namespace), the fault-injecting strategy's three static fields
    (`_camelCase`), and the Task-returning test helpers the PR retyped (`PutAsync`, `RowsAsync`,
    `PlanAsync`, `IdsAsync`).

  *Gate, as run:* a no-incremental build of every Database project but Database.Testing's tests,
  the SampleHost fixture (they need a local SDK pack) and the stray `Cache/src` test csproj, plus
  Sdk.Database, has no Database warning but CS2008 on Database.Refs (the only other warnings are
  the DependencyInjection and Configuration libraries' own); every Database suite passes with its
  baseline count (Database.Tests 101, Language 105, Types 93, Storage 293, Transactions 108,
  Indexing 75, Execution 2, Protocol 21, Security 5, Sql 1089, Sql.Language 999, Sql.Catalog 47,
  Sql.Schema 38, Sql.Storage 14, Sql.Client 314, Documents.Language 288, Documents.Storage 31,
  Graph 396, Graph.Language 387, Graph.Catalog 19, Graph.Storage 17, Graph.Client 57, Blob 130,
  Blob.Catalog 5, Blob.Storage 14, Blob.Client 21, KeyValuePair 186, KeyValuePair.Catalog 4,
  KeyValuePair.Storage 3, KeyValuePair.Client 10, Client 41, Hosting 53, Embedded 4,
  ApplicationModel 15, Sdk.Database 18; Documents.Client has no tests) apart from Documents.Tests,
  which grows from 153 to 189 (21 composition tests, eleven session and transaction contract
  tests, two of them added by the review, three engine tests and one scope test), and
  Documents.Catalog.Tests, which grows from 8 to 9 (the index members' argument checks). Among
  them are the #1225 and #1226 suites, in process only
  (Documents has no wire server or client): `DocumentTransactionFailureTests`,
  `DocumentStorageOperationsTests`, `DocumentWorkerResilienceTests` and `DocumentLifecycleTests`;
  the crash and recovery fixture (`DocumentProcessTests`); and the content-packing regressions of
  `548d7dff` (Documents.Storage's `DocumentChunkPlacementTests` and
  `DocumentStorageOperationsTests.PutAsync_SmallDocumentsAutoCommitted_ShouldShareDataPages`).
  Studio builds clean and its `--smoke` run gives 83 passed, 0 failed, 1 skipped (the Documents
  embedded steps among the passes); the dependency graph check passes (no reference changed); and
  the Database runtime producer packs.
- **Blob, as landed (re-verified 2026-10-06 against the code after the Documents PR).** Two
  commits on `refactor/L03.02.01.56.05-concrete-types-p4-blob`, based on the integration branch's
  `b6b669c2`: the child root, then the model. Rows 53 to 57 held against the code, with the
  readings their entries record (row 54's, §5.2's `CreateBuilder` and §6.6's line numbers had
  moved), and the leaves landed as the P4 bullets say:
  - *Leaves.* The engine, database, session, transaction and server are public sealed leaves of
    the bases; the database, session, transaction, container and builder left `Internal/` for the
    `RootNamespace`. The server session stays an internal sealed leaf (row 11), the four workers
    stay internal sealed, and `BlobDatabaseServerContext` stays for the `Context` bridge until P6
    (row 10).
  - *The shared pump.* Not compiled, through `COHESION_DATABASE_ENGINE_PUMP_IN_BASE` (row 7).
  - *The composition verb and the builder* (rows 6 and 55, §5.2): typed, and composing through
    `Compose` (§6.5); one builder (Sql) still uses the bridge.
  - *Children:* `BlobCatalog` is sealed (row 57), in a commit of its own, and the Blob and
    Blob.Catalog `Abstractions/` folders are gone. Blob.Storage had no interface row and needed
    nothing (`BlobStorage` was already a sealed `Storage` leaf); Blob.Security declares no type.
    The Blob.Client rows (58 and 59) are P5's; this PR changed only the client's tests (the
    harness's typed database, the server's `Sessions`, the wire refusal's message) and its
    streaming fixture's cast.
  - *Option B* (§6.6): the session runs the four session-bound container operations, and
    `session.Database` is the unbound database; the view is deleted. `GetOwnershipAsync` is a
    member of `BlobContainer` (§5.2).
  - *The server* (row 9): a leaf of `DatabaseServer`; its start core disposes the listener before
    an engine-state refusal propagates.
  - *Behavior.* §6.4's Blob accounting, item by item, and §6.5's composition paths.
  - *Studio.* `BlobWorkspace` runs its container and blob tools on the session (option B) through
    a downcast from `ModelWorkspace`'s root-typed session, which P7's retype removes.
  - *Fixtures.* The streaming fixtures' `(IBlobDatabase)` casts are gone (Blob's and Blob.Client's).
  - *Docs.* Blob `DESIGN.md` ("Concrete types", and the sections that named the interfaces, the
    session-bound view, the extension or the server's start) and `OVERVIEW.md`, Blob.Catalog's
    `DESIGN.md` and `OVERVIEW.md`, the root `DESIGN.md` ("Root bases") and the area record's model
    table.
  - *Interim choices pending owner confirmation.* Three questions stay open; this PR takes the
    interim choice each names, and each is recorded where it lands:
    30. **The server session's protocol version and principal** are set through the P3 base's
        protected one-shot setters, as the KeyValuePair and Graph leaves set them (row 11; the P3
        owner question 1 on rule 6 stays open).
    31. **A server start refused because the engine is not `Running` is terminal**, as the
        `DatabaseServer` base makes every failed start: Blob's retry after such a refusal is gone,
        and `StartCoreAsync` disposes the listener before the refusal propagates (row 9, §6.4; the
        P3 owner question 2).
    32. **`BlobDatabase` keeps its sessionless autocommit container operations**, mirroring the
        Documents landing, with the same documented self-wait: a write through `session.Database`
        while the session's transaction has written waits for that transaction's writer lock
        (row 54, §6.6).
  - *The review's fixes.* Three reviews; two approved with minor findings only, one asked for
    changes over one major finding. **Major:** option B let `session.Database.DisposeAsync()` leave
    the engine `Faulted` for good, and with it the Blob server refusing every start, connection
    and handshake: the engine keeps a database its holder closed registered, its `IsOpen` read
    only that registration, and the version-purge worker failed on the closed database's disposed
    coordinator every pass (a probe at `b6b669c2` stayed `Running`; at the PR's head it went
    `Faulted` with 60 purge failures in 1.5 s). Fixed in code rather than recorded as a fourth
    owner question: `BlobDatabase.IsClosed`, `BlobDatabaseEngine.IsOpen` false for a closed
    database and its storage, and the version-purge worker skips a closed database (§6.4, §6.6),
    pinned by
    `BlobDatabaseServerTests.DisposeAsync_SessionDatabase_ShouldLeaveTheEngineRunningAndItsServerServing`,
    which fails without the fix (the handshake is refused). The fix also covers the direct-dispose
    path the model had before P4. The session's, the database's and Blob `DESIGN.md`'s remarks
    say so. The other engines carry the same `IsOpen` (Documents' `DocumentDatabaseEngine.cs:117`,
    reachable through `session.Database` since its own option-B landing; Graph's, KeyValuePair's
    and Sql's for a directly disposed database). They are outside this model PR, so a follow-up
    applies the same fix to each (Documents first). **Minor:** the last
    `(BlobDatabaseEngine)builder.Build()` identity cast is gone (`BlobStorageOperationsTests`); `BlobDatabaseServerOptions.ShutdownDrainTimeout` names
    `DatabaseServer.StopAsync` instead of the interface P6 deletes; the database's class remarks
    name its four container operations with their parameter lists (each has an internal overload,
    so the bare crefs were ambiguous); `BlobCatalog`'s four mutation members document the
    `BlobCatalogException` a malformed persisted record raises, and its `Open` remark no longer
    says the catalog keeps neither the storage nor the coordinator (it keeps both and owns
    neither; Blob.Catalog's `DESIGN.md` likewise); the primary-constructor `BlobRequest` the PR
    declared in two test files is one test object with an ordinary constructor
    (`TestObjects/BlobRequest.cs`); the renamed snake_case engine test follows the naming rule
    (`BeginTransactionAsync_SessionContainerOperations_ShouldRollBackAndCommitChunksAndCatalog`,
    with a display name); the using-directive groups of the twelve test and fixture files the PR
    touched that broke the order (System, third-party, Cohesion, each group apart); and the
    Task-returning test helpers the PR retyped (`WriteAsync`, `ReadAsync`, `NamesAsync`, the
    cross-file callers of `BlobEngineTests`' included). None was rejected.

  *Gate, as run:* a clean build of every Database project but Database.Testing's tests, the
  SampleHost fixture (they need a local SDK pack) and the stray `Cache/src` test csproj, plus
  Sdk.Database, has no Database warning but CS2008 on Database.Refs (the only other warnings are
  the DependencyInjection and Configuration libraries' own); every Database suite passes with its
  baseline count (Database.Tests 101, Language 105, Types 93, Storage 293, Transactions 108,
  Indexing 75, Execution 2, Protocol 21, Security 5, Sql 1089, Sql.Language 999, Sql.Catalog 47,
  Sql.Schema 38, Sql.Storage 14, Sql.Client 314, Documents 189, Documents.Language 288,
  Documents.Catalog 9, Documents.Storage 31, Graph 396, Graph.Language 387, Graph.Catalog 19,
  Graph.Storage 17, Graph.Client 57, Blob.Catalog 5, Blob.Storage 14, Blob.Client 21,
  KeyValuePair 186, KeyValuePair.Catalog 4, KeyValuePair.Storage 3, KeyValuePair.Client 10,
  Client 41, Hosting 53, Embedded 4, ApplicationModel 15, Sdk.Database 18) apart from Blob.Tests,
  which grows from 130 to 167 (21 composition tests, ten session and transaction contract tests,
  the session-closed commit pinned for an aborted transaction too, three engine tests, the
  server's terminal start refusal, and the review's closed-database test); Blob.Client keeps its
  21, one of them now pinning the server's BEGIN refusal. These are the counts of the full rerun
  after the review's fixes. Among them are the #1188, #1225 and #1226 suites in process
  (`BlobTransactionFailureTests`, `BlobLifecycleTests`, `BlobStorageOperationsTests`,
  `BlobEngineTests`) and over the wire (Blob.Client's `BlobTransactionFailureWireTests` and
  `BlobClientFailureTests`, `BlobDatabaseServerTests`); the worker-resilience and holder-drain
  tests (`BlobWorkerResilienceTests`); the crash and streaming fixtures (`BlobProcessTests`,
  `BlobClientProcessTests`); and the content-packing regressions of `548d7dff` (Blob.Storage's
  `BlobChunkPlacementTests` and
  `BlobStorageOperationsTests.OpenWriteAsync_SmallBlobsAutoCommitted_ShouldShareDataPages`). Studio
  builds clean and its `--smoke` run gives 83 passed, 0 failed, 1 skipped (the Blob embedded and
  wire steps among the passes); the dependency graph check passes (no reference changed); and the
  Database runtime producer packs.
- **The closed-database follow-up, as landed (2026-10-06).** Blob's review fix applied to the
  KeyValuePair, Graph and Documents engines (§12), on `fix/database-closed-outside-engine` from the
  integration branch's `e092cada`, one commit per model; Sql's is the Sql PR's.
  - *Reproduced first.* Each model got a test that closes a database outside the engine under
    20 ms worker intervals, lets the workers pass over it for half a second, runs one more pass of
    each, and asserts that no worker records a failure and the engine stays `Running`. At
    `e092cada` all four cases failed on the version-purge worker alone, its full pass reaching the
    closed database's disposed `TransactionManager` (`ObjectDisposedException` from
    `RetryDeferredUndoAsync`): Documents 21 failed passes closed directly and through
    `session.Database`, Graph 21, KeyValuePair 24. The flush, write-back and checkpoint workers
    recorded nothing. The KeyValuePair and Graph servers do not read the engine's state, so they
    kept serving; the engine's `Faulted` reached `Database.Hosting`'s health, degraded for good.
  - *The fix, Blob's pattern.* An internal `IsClosed` on each database (the base's disposed flag);
    each engine's `IsOpen` overloads false for a closed database (and, for Graph and Documents, its
    storage); and every worker that visits databases skips a closed one: the version-purge worker
    in its pass and its trigger wait, and KeyValuePair's flush and write-back workers, which visit
    databases (both file sets each) where Graph's, Documents' and Blob's visit storages: those
    still visit a closed database's storage and tolerate its `ObjectDisposedException` through
    `IsOpen(storage)` (write-back writes nothing for a disposed storage). The checkpointer's pass is
    the engines' shared one (`Database/shared/DatabaseCheckpointWorker.cs`, unchanged); as first
    landed it only tolerated a closed database's `ObjectDisposedException` through `IsOpen`, as
    Blob's does, and the review made each model's checkpointer skip a closed database ("Review, as
    applied", below). The reopen is unchanged: Documents and Graph refuse it with
    `ObjectDisposedException` until the database is dropped, and KeyValuePair returns the closed
    instance, which refuses a session. PostgreSQL's background workers treat a dropped object the
    same way, skipping it quietly rather than failing on it (autovacuum,
    `src/backend/postmaster/autovacuum.c:998-1000`, `:1859-1868`, `:2510-2513`; the checkpointer's
    canceled fsync requests, `src/backend/storage/sync/sync.c:400-411`, `:492-503`); the close here
    happens outside the engine, so the workers read the database's own flag.
  - *Tests.* `DocumentWorkerResilienceTests.DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunning`
    (a theory: directly and through `session.Database`; the other database takes writes),
    `GraphWorkerResilienceTests` and `KeyValueWorkerResilienceTests`'
    `DisposeAsync_DatabaseClosedOutsideTheEngine_ShouldLeaveTheEngineRunningAndItsServerServing`
    (a server over the engine starts and serves a handshake and a write to the other database).
    Each also pins the reopen. They pass with the fix, eight runs in a row each.
  - *Docs.* Each model's `DESIGN.md` (Documents' and Graph's "Lifecycle" section, KeyValuePair's
    "Engine-owned background workers"), the three databases' and engines' remarks, §6.4's
    accounting of each model, §6.6 and §12; the review added `DocumentDatabaseSession`'s remarks.
  - *Review, as applied (2026-10-06),* on `fix/database-closed-outside-engine-review`, on top of
    the three model commits. One review approved with three minor findings; the other required a
    change for one major finding.
    - *The checkpointer still visited a closed database (major).* The shared pass checks only
      `IsOffline` before it begins a database and asks the model's `IsCheckpointDue`. A close that
      is not idle leaves the journal untruncated: when its retry of a deferred undo still fails,
      `TransactionCoordinator.DisposeAsync` keeps that writer in flight (#1226), and the closed
      storage stays due. When the checkpointer already had a failure recorded for the database,
      every poll then handed a lane a checkpoint the storage refused
      (`StorageTransactionException`), which the pass reports as unfinished, so the record never
      ended and the engine stayed `Faulted`; without a record, every poll still ran that doomed
      checkpoint. `IsOpen` covers only `ObjectDisposedException`, so it missed this path.
      Reproduced in all three models with a test that failed at the implementer's head `d197a647`:
      the engine `Faulted` for the test's 30 seconds, the checkpointer still holding the injected
      page-write failure from before the close. Fixed model-locally, so the shared pass, and with
      it Sql and Blob, is unchanged: `DocumentCheckpointWorker`, `GraphCheckpointWorker` and
      `KeyValueCheckpointWorker.IsCheckpointDue` are false for a closed database, so the pass begins
      it, finds nothing due, and a record ends with the first pass after its backoff. PostgreSQL's
      checkpointer likewise checks a request's cancellation before it syncs the file: a canceled
      request is never synced and is dropped from the table (`src/backend/storage/sync/sync.c:400-411`,
      `:464-466`). Tests: each model's
      `CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
      (a checkpoint failure recorded for a database whose page writes fail, then a close with a
      rolled-back transaction's undo deferred behind a bracket that holds every page; the failure
      ends, the engine runs again, nothing is offline), five runs in a row each.
    - *Docs (minor, both applied).* The models' `DESIGN.md`, §6.4's three accountings and this block
      said the checkpointer "inherits the skip through `IsOpen`"; they now say it skips through
      `IsCheckpointDue`, and that Documents' and Graph's flush and write-back workers still visit a
      closed database's storage and tolerate its `ObjectDisposedException`.
      `DocumentDatabaseSession`'s class and `Database` remarks now say the workers skip the closed
      database and the engine stays `Running`, as `BlobDatabaseSession`'s do.
    - *KeyValuePair's skip comments (minor, applied).* The flush and write-back workers' new
      comments said the engine keeps a closed database "only as a name", wrong for a model whose
      reopen returns the closed instance; they now say so, and both workers' class remarks name the
      skip.
    - *Gate, as rerun after the review:* a rebuild of the same projects has no Database warning but
      CS2008 on Database.Refs (the other 101 are DependencyInjection's 99 and Configuration's 2);
      every Database suite passes with the counts above, apart from the three models', which each
      grow by the review's test: Documents 192, Graph 398, KeyValuePair 188. Studio builds clean and
      its `--smoke` run gives 83 passed, 0 failed, 1 skipped; the dependency graph check passes; and
      the Database runtime producer packs.
  - *Gate, as run:* a clean build of every Database project but Database.Testing's tests, the
    SampleHost fixture and the stray `Cache/src` test csproj, plus Sdk.Database, has no Database
    warning but CS2008 on Database.Refs (the only other warnings are the DependencyInjection and
    Configuration libraries' own); every Database suite passes with its baseline count (Database.Tests
    101, Language 105, Types 93, Storage 293, Transactions 108, Indexing 75, Execution 2, Protocol
    21, Security 5, Sql 1089, Sql.Language 999, Sql.Catalog 47, Sql.Schema 38, Sql.Storage 14,
    Sql.Client 314, Documents.Language 288, Documents.Catalog 9, Documents.Storage 31,
    Graph.Language 387, Graph.Catalog 19, Graph.Storage 17, Graph.Client 57, Blob 167,
    Blob.Catalog 5, Blob.Storage 14, Blob.Client 21, KeyValuePair.Catalog 4, KeyValuePair.Storage
    3, KeyValuePair.Client 10, Client 41, Hosting 53, Embedded 4, ApplicationModel 15,
    Sdk.Database 18) apart from the three models' own, which grow by their new tests: Documents 189
    to 191, Graph 396 to 397, KeyValuePair 186 to 187. Studio builds clean and its `--smoke` run
    gives 83 passed, 0 failed, 1 skipped; the dependency graph check passes (no reference changed);
    and the Database runtime producer packs.
  - *Blob's checkpointer, as landed (2026-10-06),* on `fix/blob-checkpointer-closed-database` from
    the integration branch's `4d1f7e49`, after the Sql PR: #1289's last remainder (§12).
    `BlobCheckpointWorker.IsCheckpointDue` reads `!database.IsClosed && …`, the guard the other
    four models carry, and the worker's remarks name the case.
    `BlobWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
    ports their test: a checkpoint failure recorded for a database whose page writes fail, then a
    close with a rolled-back transaction's undo deferred behind a bracket that holds every page
    (the close throws `StorageTransactionException` and keeps the writer in flight); the failure
    ends, the engine runs again, nothing is offline. With the guard removed the test fails (the
    engine `Faulted` for its 30 seconds, the checkpointer still holding the injected page-write
    `IOException`); with it, it passes ten runs in a row. Blob `DESIGN.md` ("Concrete types") and
    §6.4's Blob accounting now say the checkpointer skips a closed database through
    `IsCheckpointDue` (they said it inherited the skip through `IsOpen`), and that the flush and
    write-back workers still visit the closed storage: write-back writes nothing to a disposed
    storage, and a flush of one does nothing when no commit is pending and otherwise ends in an
    `ObjectDisposedException` that `IsOpen(BlobStorage)` tolerates. *Gate, as run:* the Blob test
    project builds with no warning, and Blob 168 (167 and the new test), Blob.Catalog 5,
    Blob.Storage 14, Blob.Client 21, Hosting 54 and Embedded 4 pass.
    - *Review, as applied (2026-10-06),* on `fix/blob-checkpointer-closed-database-review`. One
      review approved with two minor documentation findings, both applied. The reviewer reran the
      mutation (with the guard removed the test failed, the engine `Faulted` for 30 seconds; with
      it, ten runs in a row passed) and confirmed that an open database is never skipped: a second
      database kept being checkpointed after the other's close that was not idle.
      *§6.6 was stale (minor).* Its option-B paragraph still said the Sql PR "fixes" Sql's and did
      not name Blob's checkpointer fix; it now says the Sql PR fixed Sql's and this follow-up made
      Blob's checkpointer skip a closed database.
      *Blob `DESIGN.md` gave the wrong reason (minor).* It said the flush and write-back workers
      visit a storage "whose close flushed it", but a close whose page flush fails still disposes
      the storage (`Storage.DisposeAsync` sets `_disposed` in its `finally`) without having
      flushed it. The workers tolerate it because `Storage.WriteBackDirtyPages` returns 0 once the
      storage is disposed, and `FlushPendingCommits` either finds no commit pending or reaches
      `StorageJournal.EnsureDurable`, whose `ObjectDisposedException` `IsOpen(BlobStorage)`
      absorbs (the review's suggested wording said a flush always throws; the applied text says
      it does nothing when no commit is pending). The reviewer's probe kept page writes failing
      through the close, and every worker's failure still ended with the engine `Running`. The
      bullet also opened with "Its workers skip a closed database" and then said two of them still
      visit it; it now says they leave a closed database alone. *Gate, as rerun after the
      review:* the Blob test project builds with no warning; with the guard removed the new test
      fails (the engine `Faulted` for its 30 seconds), and with it restored it passes ten runs in a
      row; Blob 168, Blob.Catalog 5, Blob.Storage 14, Blob.Client 21, Hosting 54 and Embedded 4
      pass.
- **The Sql PR** carries:
  - §6.7, with the SDK strings in lockstep;
  - the `ExternalEngineBuilder` deletion and the builder-validation retests (row 83);
  - `IDatabaseSchemaProvisioner` kept in `SqlDatabase`'s base list until P6 (row 8);
  - the `SqlDatabaseEngineFactory` deletion and `CrashCaptureSqlStorageStrategy` made internal
    (§5.2, row 84);
  - the reversal of Sql DESIGN's 2026-10-02 ruling (`Database.Sql/docs/DESIGN.md:627-631`) and its
    "interface-first entry" paragraph (`:1554`);
  - the note that #1232 is superseded.
- **Sql, as landed (re-verified 2026-10-06 against the code after the Blob PR).** Three commits
  on `refactor/L03.02.01.56.05-concrete-types-p4-sql`, based on the integration branch's
  `e092cada`: the catalog (`47aafdfe`, rows 85 and 86), Sql.Schema behind the opaque `SqlSchema`
  (`c4d10a14`, §6.7 and rows 87 to 101, with the SDK strings in lockstep), then the model. Rows 81
  to 84 held against the code, with the readings their entries record (§5.2's `CreateBuilder`
  line had moved, and Hosting.Tests linked the fault-injecting strategy, row 84), and the leaves
  landed as the bullets above say:
  - *Leaves.* The engine, database, session, transaction and server are public sealed leaves of
    the bases; the database, session, transaction, builder and aggregate expression left
    `Internal/` for the `RootNamespace`. The server session stays an internal sealed leaf (row 11),
    the five workers stay internal sealed, and `SqlDatabaseServerContext` stays for the `Context`
    bridge until P6 (row 10).
  - *The phase-4 scaffolding* (rows 6 and 7): the bridge overload of `DatabaseEngineBuilderState`,
    `shared/DatabaseEngineWorkerPump.cs` and the four `COHESION_DATABASE_ENGINE_PUMP_IN_BASE`
    defines are deleted, and `DatabaseEngineWorker` has its release hook.
  - *The composition verb and the builder* (rows 6 and 83, §5.2): typed, composing through
    `Compose` (§6.5); `ExternalEngineBuilder` is deleted and its cases retested.
  - *Provisioning* (row 8): `SqlDatabase` keeps `IDatabaseSchemaProvisioner` in its base list until
    P6.
  - *Deleted:* the four interfaces of the model's `Abstractions/` folder and
    `SqlDatabaseEngineFactory` (§5.2); `CrashCaptureSqlStorageStrategy` is internal (row 84).
  - *Behavior.* §6.4's SQL accounting item by item, `COHSQLT005` (the coded aborted error this PR
    chose) and the closed-database fix among it, and §6.5's composition paths.
  - *Docs.* Sql `DESIGN.md` (the 2026-10-02 nesting-limit ruling and the "interface-first entry"
    paragraph reversed, each naming #1232 superseded; "The transaction's end state machine"; the
    workers' closed-database paragraph; "Concrete types") and `OVERVIEW.md`, Sql.Language's
    `DIALECT.md` (`COHSQLT005`), the root `DESIGN.md` (the shared pump, the bridge and the release
    hook), the area record's model table (every model's database is now a type), the hosting
    design's factory mention, and stale SQL type names in the Sql.Storage, Storage and Indexing
    `DESIGN.md` files and a `KeyValueDatabase` comment.
  - *Interim choices pending owner confirmation.* Decision 30 reaches SQL too: its server session
    sets the version and principal through the one-shot setters (row 11). Decision 31 does not:
    the SQL server never refused a start for its engine's state (row 9). Decision 32 is
    Documents' and Blob's alone.
  - *Owner review.*
    33. **The worker's release entry** (row 7). As first landed it was public: the worker was
        `IAsyncDisposable` with a `DisposeAsync` that did nothing on a worker an engine owns, on
        the reading that an entry internal to the root could not reach the shared builder state
        compiled into the model assemblies without a root grant. The review showed it can, and
        applied the plan's recommendation ("Review, as applied", below): no public disposal; a
        `protected static ReleaseUnownedWorkerAsync` on `DatabaseEngine`, re-exposed by each model
        engine as an internal `ReleaseRefusedWorkerAsync` and handed to
        `DatabaseEngineBuilderState.Complete` beside the compose method. What remains for the
        owner: the protected static is visible to any engine author deriving from
        `DatabaseEngine`, who can release a worker no engine owns with it (it does nothing on an
        owned one); the cost is one `Complete` parameter and one internal static per model engine.
    34. **`COHSQLT005`** as SQL's coded aborted error, with the rejected alternatives in §6.4.
    35. **Hosting's offline-database health tests no longer take a real SQL database offline**
        (§6.9): no public API takes a real database offline, so they report the offline database
        through Hosting's engine double, and the real offline path stays in the SQL suites. The
        worker-fault half runs on a real SQL engine again since the review, through a guided worker
        registered with the public typed `AddWorker`; as first landed it drove the double too, on
        the reading that a real fault needed a public fault seam on a model.
  - *Outside the rows:* the `(SqlDatabaseEngine)engine` casts inside `AddServer` factories (the
    templates, the SampleHost fixture, Database.Testing's README, Hosting's `OVERVIEW.md`, the
    hosting and developer-experience designs) are identity casts now and still compile; they are
    left for one sweep with the companion examples rather than churned here.

  *Gate, as run:* a no-incremental build of every Database project but Database.Testing's tests,
  the SampleHost fixture (they need a local SDK pack) and the stray `Cache/src` test csproj, plus
  Sdk.Database, has no Database warning but CS2008 on Database.Refs (the only other warnings are
  the DependencyInjection and Configuration libraries' own); every Database suite passes with its
  baseline count (Language 105, Types 93, Storage 293, Transactions 108, Indexing 75, Execution 2,
  Protocol 21, Security 5, Sql.Language 999, Sql.Catalog 47, Sql.Storage 14, Sql.Client 314,
  Documents 189, Documents.Language 288, Documents.Catalog 9, Documents.Storage 31, Graph 396,
  Graph.Language 387, Graph.Catalog 19, Graph.Storage 17, Graph.Client 57, Blob 167,
  Blob.Catalog 5, Blob.Storage 14, Blob.Client 21, KeyValuePair 186, KeyValuePair.Catalog 4,
  KeyValuePair.Storage 3, KeyValuePair.Client 10, Client 41, Hosting 53 (three tests rewritten,
  §6.9), Embedded 4, ApplicationModel 15, Sdk.Database 18) apart from three: Database.Tests grows
  from 101 to 102 (the release hook's test), Sql.Schema from 38 to 39 (the declaration-compile
  test of `c4d10a14`), and Sql.Tests from 1089 to 1120 (14 more composition tests, ten session
  and transaction contract tests, four engine contract tests, the wire `COHSQLT005` test, the
  server's closed-database test and one more nesting-limit case). Among them are the #1188, #1225
  and #1226 suites in process (`SqlTransactionContractTests`, `SqlTransactionRollbackTests`,
  `SqlStorageOperationsTests`, `SqlTransactionControlTests`) and over the wire
  (`SqlTransactionControlTests`' wire cases, `SqlDatabaseServerTests`, Sql.Client's suites).
  Studio builds clean and its `--smoke` run gives 83 passed, 0 failed, 1 skipped; the dependency
  graph check passes; and the Database runtime producer packs.
  Against refreshed canonical packs (`Install-Local.ps1 -UseCanonicalVersion -SkipLibraries -Rids
  win-arm64`, 59 packages at `10.0.0-preview.1`, none of them left in the global NuGet cache),
  Sdk.Database's tests pass 18 of 18 and Database.Testing's 5 of 5, among them the SampleHost
  test (`LocalGateway_WithGeneratedSampleManifest_ShouldServeAndRecoverAcrossRelaunch`), whose
  seeded `orders` table exists only if Hosting's provisioner found `SqlDatabase` through its
  `IDatabaseSchemaProvisioner` type test. The first run of the script failed in its first step,
  the rebuild of `build/Tasks`, because idle shared MSBuild nodes held that DLL open; the rerun
  used a scratch copy of the script without that step, since the task assembly was unchanged and
  already built.
  - *Review, as applied (2026-10-06),* on `refactor/L03.02.01.56.05-concrete-types-p4-sql-review`,
    on top of the three commits. One review approved with four minor findings; two required
    changes, one for each major finding. Every finding was applied; none was rejected.
    - *Merged first.* The integration branch's closed-database follow-up for KeyValuePair, Graph
      and Documents (`9acddea5`) landed after this PR branched, and this PR edits the shared
      checkpoint worker and the worker base those fixes run on, so the review merged it before
      its fixes and gates the combined tree. The one conflict was §12's closed-database bullet;
      the integration side's text is kept, with Sql's fix and checkpointer guard marked done.
    - *The checkpointer's closed-database guard had no test (major).*
      `SqlCheckpointWorker.IsCheckpointDue` reads `!database.IsClosed && …`, but neither
      closed-database test reached it: it matters only when a close keeps a writer in flight
      (#1226), where the storage refuses the checkpoint with `StorageTransactionException`, which
      `IsOpen` does not cover. `SqlWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
      ports the test the three models carry; with the guard removed it fails (the engine
      `Faulted` for 30 seconds, the checkpointer holding the injected `IOException`), and with it
      it passes. The checkpointer's remarks and Sql `DESIGN.md` name the case (§6.4, §12). That
      left Blob's checkpointer the last without the guard; it landed after this PR (the
      closed-database follow-up, "Blob's checkpointer").
    - *`SqlSchemaCompiler` outside `Internal/` (major).* Made internal but left in the Sql.Schema
      `src/` root under the public namespace; it moved to `src/Internal/` under
      `Assimalign.Cohesion.Database.Sql.Schema.Internal` (§5.2), and `CompiledSchemaTests` imports
      that namespace. The area record's `DESIGN.md` no longer calls it public.
    - *The worker's release entry, internal-only (minor, three reviews).* Owner-review item 33
      said an internal-only entry was impossible without a root grant; the reviews showed the
      shared builder state reaches a root release the way it reaches the leaf's compose method.
      Applied as row 7 records: `DatabaseEngine.ReleaseUnownedWorkerAsync` (protected static), an
      internal `ReleaseRefusedWorkerAsync` on each of the five model engines, a third
      `Complete` parameter, and no `IAsyncDisposable` or public `DisposeAsync` on the worker. The
      five model composition-contract tests pass the release to `Complete` as their builders do.
    - *The ownership guard's gaps (minor, three reviews).* `AttachWorker` marked a worker owned
      only after its pump thread started and accepted a worker that was not free: a released one
      was pumped with its lanes disposed, and one another engine owned was pumped by both and
      released by the first engine's disposal while the second still pumped it. The claim now
      comes before the thread starts, a worker that is not free is refused with
      `InvalidOperationException`, and a failed start returns the worker to free
      (`DatabaseEngineTests.AttachWorker_ReleasedWorker_ShouldBeRefused`,
      `AttachWorker_WorkerAnotherEngineOwns_ShouldBeRefused`).
    - *Hosting's health test (minor, three reviews).* Owner-review item 35 said a real fault
      needed a public fault seam; the public typed `AddWorker` is one for a worker fault. The
      test is split: the worker half runs on a real SQL engine and reads its fold
      (`CheckAsync_WorkerFaultsOnARealEngine_ShouldFollowTheWorker`), the offline half keeps the
      double (`CheckAsync_OfflineDatabase_ShouldBeUnhealthyUntilTheReopen`), and the double's
      `Report(EngineState)` setter is deleted (§6.9; item 35 narrowed).
    - *Minor, applied.* `SqlCatalog`'s public members document the `ArgumentNullException`,
      `ArgumentOutOfRangeException` and `OperationCanceledException` they throw, and its remarks
      name both `Open` overloads; the collation overload of `CreateDatabaseAsync` points at the
      base's check order and the engine contract test pins it (a blank name and a null collation
      before disposal, a canceled token before anything is created); four Sql test names lead
      with the method (`ExecuteAsync_TransactionEndedByTheKernel_…`,
      `ExecuteAsync_OnOfflineDatabase_…`, `CreateDatabaseAsync_TypedMembers_…`,
      `ExecuteAsync_WireTransactionEndedByTheKernel_…`), while the names that mirror the sibling
      models' (`Members_…`, `EndAsync_…`, `BeginAndExecute_…`) stay; `SqlDataStorageFormatTests`'
      reopening strategy double has an explicit constructor instead of a primary one; the SDK
      extractor's four private consts are camelCase; `SqlDatabase`, `SqlDatabaseSession`,
      `SqlDatabaseTransaction`, `SqlDatabaseEngine` and `SqlStorageStrategy` put their usings
      before the namespace; root `DESIGN.md` loses its blank lines inside the bullet list and
      its stray indents and lists `COHSQLT005`; Sql `DESIGN.md` rejoins a split paragraph; and
      KeyValuePair.Catalog's `DESIGN.md` names `SqlCatalog`, not the deleted `DefaultSqlCatalog`.
    - *Gate, as rerun after the review:* a no-incremental build of the same projects has no
      Database warning but CS2008 on Database.Refs (the others are DependencyInjection's 198 and
      Configuration's 4 lines); every Database suite passes: Database.Tests 104 (the two attach
      refusals), Sql.Tests 1121 (the checkpointer test), Hosting 54 (the split health test),
      Documents 192, Graph 398 and KeyValuePair 188 (the merged follow-up's tests), and every
      other suite at the count above (Sql.Schema 39, Sql.Catalog 47, Sql.Client 314, Blob 167,
      Graph.Client 57, Embedded 4, ApplicationModel 15, Sdk.Database 18 against the local
      build). Studio builds with no warning and its `--smoke` run gives 83 passed, 0 failed,
      1 skipped; the dependency graph check passes; and the Database runtime producer packs.
      Against canonical packs refreshed from the review's head (`Install-Local.ps1
      -UseCanonicalVersion -SkipLibraries -Rids win-arm64`, 59 packages at `10.0.0-preview.1`,
      none of them in the global NuGet cache before the runs; the script's first step ran this
      time, with node reuse off), Sdk.Database's tests pass 18 of 18 and Database.Testing's 5 of
      5, the SampleHost provisioning test among them.

The template, fixture and example casts `(SqlDatabaseEngine)engine` become identity casts and
still compile. *Gate:* each model's suites, including its #1188, #1225 and #1226 suites in
process and over the wire (§6.4), and Studio's build. The Sql PR also runs the `Sdk.Database`
tests against refreshed canonical packs, and the SampleHost provisioning test.

**P5, #1261: clients.** Rows 48 to 52, 58, 59, 71, 72, 78 to 80 and 102 to 104.

- **The Database.Client core first.** Then the Sql, KeyValuePair, Graph and Blob clients.
- **Extension containers.** `ExecuteStreamingAsync` folds into `DatabaseClient`, and
  `SqlProtocolConnectionExtensions` moves to an `extension(DatabaseConnection)` block (§5.2).
- **Retypes.** The Studio client workspaces, and
  `sdks/Assimalign.Cohesion.Sdk.ApplicationModel/Tasks/tests/TestProjects/EnabledWeb/Program.cs:20`
  (`ISqlClient` becomes `SqlClient`).

*Gate:* the client suites over the wire, the Studio build, and the Sdk.ApplicationModel tests.

**P6, #1262: composition retype and root interface removal.**

- **Retypes.** Rows 3 and 4. In Hosting: `DatabaseApplicationContext`, `DatabaseApplicationBuilder`
  (`server.Context.Engine` becomes `server.Engine`), `DefaultDatabaseProvisioner.cs:49` (which
  becomes a flag check; `SqlDatabase` and Hosting's `ProvisioningDatabase` double,
  `ProvisioningEngine.cs:101`, drop `IDatabaseSchemaProvisioner`), `DatabaseResourceCommandHandler`
  and the admin endpoint. Also Embedded (§5.2) and Testing. The shared builder state collapses to
  `DatabaseEngineBuilderState<TEngine> where TEngine : DatabaseEngine`, with the products fixed to
  `DatabaseEngineWorker` and `DatabaseServer` (row 6).
- **Doubles and deletions.** The Hosting and Embedded doubles move to the bases (§6.9). Then delete
  the 10 root interfaces (rows 1 and 5 to 13, which include `IDatabaseServerContext`), the four
  context classes, the five double contexts, and the bridge's explicit implementations.
- **Rule text.** Retype the `AddEngine` signature in `resource-areas.md`'s "Not yet migrated:
  Database.Hosting" paragraph.

*Gate:*

- COHRES001, COHRES002 and COHRES004;
- `dotnet pack` of the Database runtime producer, for the framework closure;
- the Hosting, Embedded and Testing suites, and the SampleHost fixture.

**P7, #1263: ApplicationModel, templates, Studio and cohesion-examples.**

- **ApplicationModel is verify-only.** The descriptor is kept and the COHAM001 closure contains no
  engine types. Run the `Sdk.Gateway` CommandGateway test project (`Program.cs:11` uses the kept
  `IDatabaseResourceDescriptor`).
- **Casts.** Remove the identity casts in the five template `Program.cs` files (for example
  `tooling/templates/.../cohesion-database/Program.cs:16`) and in the SampleHost fixture
  (`Program.cs:20`).
- **Studio.** `StudioEngines.cs:34-42` indexes a root-typed collection, so its casts remain
  downcasts unless it stores typed fields; give it typed fields. `BlobWorkspace` and
  `DocumentWorkspace` are cleaned up.
- **cohesion-examples.** Update its seven `Program.cs` files in the same window (`workflow.md`).

*Gate:* the template tests, the Studio `--smoke` build, and the cohesion-examples smoke against
fresh packs.

**P8, #1264: per-row result types and docs sweep.**

- **Result types.** §5.3's P8 rows: `QueryRow`, `QueryResultSet`, `QueryResult` (§6.8),
  `QueryRequest`, `QueryParser` and `QueryAnalyzer`.
- **Docs.** The `docs/Assembly` pages that name interfaces (Blob.Client, Graph, Graph.Catalog and
  Graph.Storage), per-project OVERVIEW and DESIGN leftovers, the area README project map,
  `docs/DEPENDENCIES.md` (regenerated), and the cohesion-docs mirror.
- **Decisions.** Re-raise the analyzer decision (§11).
- **Cleanup.** Fold this plan's durable parts into the DESIGN files and delete it.

*Gate:* `rg "public interface" resources/Database --glob '**/src/**'` returns exactly the five
kept interfaces.

## 8. Deviation markers

There are 28 markers, each with the exact text in `database-area.md`:

- **18 public abstract bases**: the 16 of §5.1, the existing `DatabaseEngineWorker`, and the
  existing `Storage`, which P1 strips of its interface. P1 marked `StorageJournal` too; P2
  collapsed it into one sealed type and removed the marker (§5.3);
- **5 model engines**;
- **5 model engine builders**.

Each base meets one of rule 2's cases: **V**, a variant set (two or more shipped
implementations); **H**, `Database.Hosting` must stay model-agnostic under COHRES002; **S**, an
inverted seam (a lower assembly drives it, and a higher assembly or the application implements
it). [Certain] The implementers were counted with rg on 2026-10-04.

| Base | Case | Implementations | Constructor | Marker in |
|---|---|---|---|---|
| `DatabaseEngine` | H, V | the five model engines | `protected` | P3 |
| `DatabaseInstance` | H, V | the five model databases | `protected` | P3 |
| `DatabaseSession` | V | the five model sessions | `protected` | P3 |
| `DatabaseTransaction` | V | the five model transactions | `protected` | P3 |
| `DatabaseServer` | H, V | the Sql, KeyValuePair, Graph and Blob wire servers | `protected` | P3 |
| `DatabaseServerSession` | V | one per model server | `protected` | P3 |
| `DatabaseEngineWorker` | H, V | 22 workers in the model assemblies | `protected` | P3 |
| `Storage` | V | `SqlStorage`, `KeyValueStorage`, `GraphStorage`, `DocumentStorage`, `BlobStorage` | `protected` (existing) | P1 |
| `StorageJournal` | none | `StreamJournal` only; collapsed into one sealed `StorageJournal` at P2 (§5.3), so its P1 marker was removed | — (sealed) | P1, removed P2 |
| `TransactionRecordSpace` | V, S | five model record spaces; the coordinator drives them | `protected` | P2 |
| `RecordVersionIndex` | V, S | Indexing's `BTreeRecordVersionIndex` and the Documents.Catalog and Graph.Storage undo indexes | `protected` | P2 |
| `VersionStore` | V | `RecordSpaceVersionStore` and `InMemoryVersionStore`, both in Transactions | `private protected` | P2 |
| `ProtocolFrameReader` | V | four, in Protocol, Database.Client and Blob.Client | `protected` | P2 |
| `ProtocolFrameWriter` | V | four, in the same three assemblies | `protected` | P2 |
| `DatabaseAuthenticator` | S | one shipped (`AllowAll`); the model servers call it and the application supplies its own | `protected` | P2 |
| `DatabaseProtocolExchange<TResult>` | V, S | the Sql, KeyValuePair, Graph and Blob exchanges and Database.Client's streaming exchange; `Database.Client` runs them | `protected` | P5 |
| `DatabaseStreamingExchange` | S | one shipped (Blob.Client's download exchange); `Database.Client` runs it | `protected` | P5 |
| `SqlClientObserver` | S | none shipped; the application sets one through `SqlClientOptions` (`SqlClientOptions.cs:29`) | `protected` | P5 |
| `KeyValueClientObserver` | S | none shipped; set through `KeyValueClientOptions` (`KeyValueClientOptions.cs:29`) | `protected` | P5 |

Internal abstract bases (the strategies and `TransactionLog`) are not public API and carry no
marker. Other sealed leaves are covered by `database-area.md`. Each marker lands in the phase that
writes its type, or, for `Storage`, strips its interface.

**At P3:** the seven P3 markers landed: the six root bases and `DatabaseEngineWorker`. The case
column holds for each as written; `DatabaseServerSession`'s leaves (one per model server) still
implement the interface until their model's P4 PR. `CompiledSchema` carries none (§5.3).

**At P4, Sql:** the model's two markers landed, on `SqlDatabaseEngine` and
`SqlDatabaseEngineBuilder`, completing the five engine and five builder markers. Its other new
public leaves (`SqlDatabase`, `SqlDatabaseSession`, `SqlDatabaseTransaction`, `SqlDatabaseServer`,
`SqlAggregateExpression`, and from its child roots `SqlCatalog`, `SqlCatalogSnapshot`, `SqlSchema`
and the four Sql.Schema builders) carry none, and neither does the internal `SqlStorageStrategy`.
`DatabaseEngineWorker`'s P3 marker stands; its release hook is a lifecycle hook under rule 4.

**At P4, Blob:** the model's two markers landed, on `BlobDatabaseEngine` and
`BlobDatabaseEngineBuilder`. Its other new public leaves (`BlobDatabase`, `BlobDatabaseSession`,
`BlobDatabaseTransaction`, `BlobDatabaseServer`, `BlobContainer`, `BlobCatalog`) carry none, and
neither does the internal `BlobStorageStrategy`.

**At P4, Documents:** the model's two markers landed, on `DocumentDatabaseEngine` and
`DocumentDatabaseEngineBuilder`. Its other new public leaves (`DocumentDatabase`,
`DocumentDatabaseSession`, `DocumentDatabaseTransaction`, `DocumentCollection`, `DocumentCatalog`)
carry none, and neither does the internal `DocumentStorageStrategy`.

**At P4, Graph:** the model's two markers landed, on `GraphDatabaseEngine` and
`GraphDatabaseEngineBuilder`. Its other new public leaves (`GraphDatabase`, `GraphDatabaseSession`,
`GraphDatabaseTransaction`, `GraphSchema`, `GraphCatalog`, `GraphStore`) carry none, and neither
does the internal `GraphStorageStrategy`.

**At P4, KeyValuePair:** the model's two markers landed, on `KeyValueDatabaseEngine` and
`KeyValueDatabaseEngineBuilder`. Its other new public leaves (`KeyValueDatabase`,
`KeyValueDatabaseSession`, `KeyValueDatabaseTransaction`, `KeyValueCatalog`,
`KeyValueCatalogSnapshot`) carry none: `database-area.md` covers them. The internal
`KeyValueStorageStrategy` carries none (internal abstract bases are not public API).

## 9. Performance evidence

[Likely] These are dispatch microbenchmarks, in ns per call, with NativeAOT on win-arm64 and
.NET 10.0.12. The design ran best-of-5 × 200M calls on a dependent chain. The critique reran the
same binary twice (`%TEMP%\wf-design-precedents\bench\aot\bench.exe`). Run-to-run variance is about
±60%, so the numbers are ranges, not points.

| Case | Design run | Critique reruns |
|---|---|---|
| Sealed, direct call | 0.43 | — |
| Interface, 1 implementation | 0.39–0.43 | — |
| Abstract, 1 implementation | 0.34–0.42 | — |
| Interface, 3 implementations, monomorphic site | 0.415 | — |
| Interface, 3 implementations, rotating | 1.43 | 1.276, 0.715 |
| Interface, 5 implementations, monomorphic | 2.45–3.09 | 2.273 |
| Abstract, 5 implementations, monomorphic | 2.29–2.63 | 3.028, 1.866 |
| Interface, 5 implementations, megamorphic | 3.63–4.75 | 2.915, 4.708 |
| Abstract, 5 implementations, megamorphic | 2.22–3.04 | 2.223, 2.485 |

**What the numbers support.** Past the guarded-devirtualization limit (§3), an abstract call
beats an interface call by about 1.5 ns. A model-level call happens once per operation, so that
gap does not show in engine throughput. Sealing the single-implementation kernel types removes
the guard check, which is a small gain.

**What they do not support.** They do not show that "anything at three or fewer types
devirtualizes whatever the type shape": the benchmark has no abstract case with three or fewer
types. They also do not show any effect at row level. `QueryRow` and `QueryResultSet` are the
per-row sites, and P8 measures them before changing their shape.

## 10. Size estimate

[Certain] Measured with rg on 2026-10-04, before this plan was written, against the 101
interfaces the program removes:

| Measure | Count |
|---|---|
| `Abstractions/I*.cs` files deleted | 101 of 106 |
| Database `src` `.cs` files naming a removed interface | 308 of 787 |
| Database `tests` `.cs` files | 182 |
| Database `fixtures` `.cs` files | 4 |
| `tooling` and `sdks` `.cs` files (Studio, templates, `Sdk.Database` extractor, `Sdk.ApplicationModel` test) | 13 |
| In-repo Markdown files | 62 |
| cohesion-examples `Program.cs` files with a model cast | 7 |
| cohesion-docs files (103 under `docs/dotnet-apis/resources/database`) | 112 |
| Projects deleted in P1 | 7 |

That is roughly 570 files in this repository and 119 in the two companions.

**Public types.**

- [Certain] 101 interfaces leave.
- [Likely] About 35 internal implementations become public sealed types under new public names:
  the design's 31, plus the four storage sub-components the review moved to sealed (rows 31, 34,
  37 and 38). About 15 static factory classes become sealed types under their existing names.
- There are 16 public abstract bases. Three reuse an existing public name (`VersionStore`,
  `DatabaseAuthenticator`, `RecordVersionIndex`).
- The corrections remove further public types: the nine Sql.Schema declaration records stay
  internal, two strategies become internal, and `SqlSchemaCompiler`, `ProtocolFraming`, the
  public `TransactionLog` factory, `QueryStatementResult` and `SqlDatabaseEngineFactory` go.
- [Likely] Net: about 53 to 58 fewer public types.

**Effort.** [Guessing] 14 PRs in this repository: P0, P1, P2 (five commits), P3, P4.0, five model
PRs, P5, P6, P7 and P8. P7 adds one cohesion-examples PR in the same window. Roughly 15,000 to
25,000 changed lines, most of them mechanical renames in tests. The real design work is in P3
(NVI bases and the consolidated state machine), §6.6 and §6.7. The moves out of `Internal/` (C12)
are counted in P2, P4 and P5: [Likely] about 35 files, including the four storage
sub-components, each changing namespace, plus the `using …Internal` lines in their tests.

## 11. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | [Likely] The owner expects throughput. The measured dispatch gain is about 1.5 ns per megamorphic call, within ±60% run-to-run variance (§9). | §1 and §9 state it. #1236 is the throughput work and runs first. |
| R2 | [Certain] The SDK breaks silently. The extractor matches by string, so a rename compiles and then rejects every schema at build time. | The SDK strings change in the same commit (§6.7), and the SDK tests run against refreshed canonical packs. |
| R3 | [Likely] A `new` typed async member calls the `Core` directly and skips the state machine. | `database-area.md` rule 7. Reviewers check every `new` member. An analyzer remains an open option. |
| R4 | [Certain] Centralizing the messages changes behavior: tests that assert the old "already active" text fail. | Each model's P4 PR updates its own assertions when its session adopts the base (§6.4). |
| R5 | [Likely] The changes conflict on the integration branch. They touch about 570 files. | #1236 lands first, model PRs are serialized, and each phase merges back before the next starts. |
| R6 | [Certain] The protected root constructors leave the hierarchy open: anyone can derive from `DatabaseEngine` and call the protected attach methods before freezing. | NVI, sealed leaves and frozen attach keep it tight. The analyzer decision is open. |
| R7 | [Certain] The #1188, #1225 and #1226 transaction contracts regress when the state machine moves into the base. | P3 tests the base through root-level doubles in Database.Tests. Each model's P4 PR, where that model adopts the base, runs its #1188, #1225 and #1226 suites in process and over the wire (§6.4). |
| R8 | [Likely] The `LockManager` filter changes the lock retention of deferred writers (#1226). | §6.2. The #1226 rollback tests gate P2. |
| R9 | [Certain] Crash-harness coverage depends on the strategies staying derivable. | The strategies become `internal abstract`, never sealed, and the crash doubles derive through the existing grants. |
| R10 | [Certain] Option B changes more Blob and Documents API than option A would: Studio's workspaces and the wire servers move to session operations. | It is done in the same model PR, with Studio built in each. |
| R11 | [Guessing] Public sealed `SqlConnection` and `SqlClient` clash with Microsoft.Data.SqlClient's types when both namespaces are imported. | Users alias one of them. Renaming is out of scope unless a consumer reports the clash. |
| R12 | [Certain] Deleting `ExternalEngineBuilder` removes the builder-validation coverage it carried. | Direct tests against the sealed builder (row 83). |
| R13 | [Certain] Studio is MAUI and Windows-only, outside most CI legs, so it can rot between phases. | It is built in P4, P5 and P7. |
| R14 | [Certain] cohesion-examples drifts. | The identity cast keeps it compiling until P7. P7 updates it in the same window. |
| R15 | [Certain] A source break for preview.1 consumers. | One release-notes line (D11). |
| R16 | [Certain] The coordinator carries three internal test hooks in production code (§6.9). | They are internal, `null` unless a test sets them, and called once per checkpoint, sequence reservation or rollback, never per row. Only Transactions.Tests reaches them, and each test asserts its hook fired. |

## 12. Follow-ups this plan does not do

- The analyzer decision (§2) is open until P8.
- Issue bodies (done 2026-10-05, each in a "Scope update" section): #1264 no longer owns
  `DatabaseEngineWorker`, which this plan moves to P3 (§5.3), and the phase-0 review changed
  other issue bodies' scope: #1255's tally becomes 28 delete, 52 sealed, 21 abstract and 5 keep (§5.1); #1257 (P1) loses rows 31,
  34, 37 and 38 to #1258 (P2) and gains the coordinator hooks and the tests §6.9 names (eight
  at P1, re-counted after #1252 renamed and split one of the original six);
  #1259 (P3) gates on a Database.Tests base suite, and each model PR under #1260 (P4) carries
  its own #1188, #1225 and #1226 gate (§6.4).
- #1232 is superseded: the Sql PR of P4 sealed the builder and reversed Sql `DESIGN.md`'s 2026-10-02 ruling and "interface-first entry" paragraph. Closing the issue is the integration merge's step (the PR's session has read-only GitHub access).
- `Storage`'s record and page operations accept a `StorageTransaction` of another storage
  instance: the argument check's message says "not created by this storage instance", but it
  only ever tested the type (found at P2, row 36). Checking the owner is a behavior change, so P2
  left it for its own fix, #1286.
- The other engines' `IsOpen` treated a database its holder disposed as open, because the engine
  keeps it registered to refuse its reopen, so a worker that touches its disposed state can fail
  every pass and leave the engine `Faulted`, as Blob's version-purge worker did (found by the
  Blob P4 review, which fixed Blob; §7, "Blob, as landed"). **Done for Documents, Graph and
  KeyValuePair** by the follow-up of 2026-10-06 (§7, "The closed-database follow-up"), each
  reproduced at `e092cada` with a test that failed (the version-purge worker, 21 to 24 failed
  passes in half a second) and fixed with Blob's pattern. **Done for Sql** by the Sql PR (§6.4,
  "Sql at P4"; §7, "Sql, as landed"), reached through a directly disposed database: with the fix
  removed its version-purge worker and checkpointer failed on such a database and the engine
  went `Faulted`.
- The checkpointer of Blob still visited a database its holder closed: the shared pass
  (`Database/shared/DatabaseCheckpointWorker.cs`) asks the model's `IsCheckpointDue`, and Blob's
  read only the storage. After a close that is not idle (a deferred undo's writer the close keeps
  in flight, #1226) the closed storage stayed due, every poll ran a checkpoint it refuses, and a
  checkpoint failure recorded for the database before the close never ended, so the engine stayed
  `Faulted` (found by the closed-database follow-up's review, reproduced for Documents, Graph and
  KeyValuePair; §7, "The closed-database follow-up", "Review, as applied"). The fix is the one
  those three landed: `BlobCheckpointWorker.IsCheckpointDue` returns false for a closed database
  (`!database.IsClosed && …`), with the test the three models carry.
  **Done for Sql** by the Sql PR: `SqlCheckpointWorker.IsCheckpointDue` reads
  `!database.IsClosed && …`, and
  `SqlWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
  ports the three models' test (§7, "Sql, as landed", "Review, as applied"). **Done for Blob**
  by the follow-up of 2026-10-06 on `fix/blob-checkpointer-closed-database` (#1289):
  `BlobCheckpointWorker.IsCheckpointDue` reads `!database.IsClosed && …`, and
  `BlobWorkerResilienceTests.CheckpointWorker_FailingDatabaseClosedWithAWriterInFlight_ShouldEndItsFailureAndLeaveTheEngineRunning`
  ports the test; with the guard removed it fails (§7, "The closed-database follow-up", "Blob's
  checkpointer"). Closed: all five models' checkpointers skip a closed database.
- `general-rules.md` still uses `IDatabase` in its XML-documentation example and its naming table,
  and `services.AddSingleton<IDatabase, Database>()` in the `extension(...)` example. Those
  examples teach unrelated rules; replace them in P6, when `IDatabase` is deleted, so they never
  name a type that does not exist.
