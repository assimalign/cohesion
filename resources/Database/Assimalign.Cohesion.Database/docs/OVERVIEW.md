# Assimalign.Cohesion.Database — Overview

The contract root of the Cohesion Data Platform: the model-agnostic abstract bases every engine
derives from and every consumer programs against — `DatabaseEngine` (a data machine managing
logical databases), `DatabaseInstance` (a logical database), `DatabaseSession` (scoped execution
context), and `DatabaseTransaction` (explicit ACID scope) — plus the server bases
(`DatabaseServer`, `DatabaseServerSession`; servers are per-model, each implemented inside its
model package) and the engine worker base (`DatabaseEngineWorker`), the application seam
(`IDatabaseApplication`, `IDatabaseApplicationContext`, `IDatabaseApplicationBuilder`, three of
the area's five kept interfaces), the model-agnostic provisioning surface (`CompiledSchema`,
`DatabaseInstance.SupportsSchemaProvisioning` and `ApplySchemaAsync`, `SchemaMigrationResult`),
object ownership (`DatabaseObjectOwner`, `DatabaseObjectLockedException`), the area's exception
root (`DatabaseException`, `DatabaseNotFoundException`, `DatabaseParseException`), and shared value
objects (`DatabaseName`, `EngineState`, `EngineModel`). The root is also the area's **rollup**: it
references every child root (Types, Language, Storage, Transactions, Execution, Indexing,
Protocol, Security), so one reference to the root delivers the whole base surface — including
child-owned vocabulary the contracts speak (`TransactionId` and `TransactionState` from
`Database.Transactions`, `ProtocolVersion` from `Database.Protocol`).

## Scope

- **Root bases (concrete-types plan, phases 3 and 6, #1259 and #1262)** — `DatabaseEngine`,
  `DatabaseInstance`, `DatabaseSession`, `DatabaseTransaction`, `DatabaseServer`,
  `DatabaseServerSession` and `DatabaseEngineWorker` are the abstract bases of the area's
  concrete-first rule: non-virtual public members over protected cores, field-backed fixed state,
  protected attach methods frozen after composition, and one copy of the behavior every model
  shares (the explicit-transaction state machine, the one "already active" check, the engine's
  worker pump, state fold and disposal order, the server lifecycle). Every model derives its
  leaves from them since phase 4, and phase 6 deleted the ten interfaces they replaced
  (`IDatabaseEngine`, `IDatabase`, `IDatabaseSession`, `IDatabaseTransaction`, `IDatabaseServer`,
  `IDatabaseServerContext`, `IDatabaseServerSession`, `IDatabaseEngineWorker`,
  `IDatabaseEngineBuilder`, `IDatabaseSchemaProvisioner`). See [DESIGN.md](DESIGN.md), "Root
  bases".
- **Engine contracts** — create/open/drop/enumerate logical databases. Engines are **data
  machines**: operational from creation, no start/stop ceremony; disposal quiesces their
  background workers and durably flushes. `State` is observational
  (`Running`/`Faulted`/`Disposed`); `Workers` exposes the engine-owned background loops for
  diagnostics (name, kind, cadence). The `DatabaseEngineWorker` base never lets a failure end its
  loop: it records a database's failure (`Fault`), skips that database for `FailureBackoff` while
  the others keep the worker's pace, and retries; `Faulted` lasts exactly while a worker holds a
  failure it has not worked off (#1268); `HasFailingWorker(name)` and `HasEngineWideFailure`
  tell one database's failure from the engine's own (owner decision 42). A database whose work
  keeps failing for `WorkerFailureWindow` (100 s by default) across at least
  `WorkerFailureMinimumPasses` failed passes (three by default, owner decision 42) is taken
  offline (owner decision 25). Failures, recoveries and give-ups are written to the
  `Assimalign.Cohesion.Database` event source.
- **Server contracts** — `DatabaseServer` (start/stop lifecycle — "running" lives on the server,
  never the engine — with the one `Engine` it fronts and its active `Sessions`). Servers are
  per-model and this base is the only area-wide requirement — each model derives its server from
  it inside its model package (`SqlDatabaseServer` in `Database.Sql`).
- **Application composition seam** — `IDatabaseApplicationBuilder` / `IDatabaseApplication` /
  `IDatabaseApplicationContext`: model packages register deferred engines against this root seam
  (e.g. `Database.Sql`'s `AddSql((context, engine) => ...)` with nested server factories) without
  knowing the hosting implementation. The seam names the root bases (`AddEngine(DatabaseEngine)`,
  `Engines`, `Servers`, `GetEngine(name)`), and the static extension `GetEngine<TEngine>(name)`
  returns a model's engine typed. Composition roots register background work through the concrete
  `DatabaseApplicationBuilder.AddService` in `Database.Hosting`; the root contracts expose no
  hosting-library types. `Database.Hosting` implements the seam
  (`DatabaseApplication.CreateBuilder(args)`) so services start before servers and stop after
  them in reverse order.
- **Model-agnostic provisioning** — `CompiledSchema` carries identity, the canonical document,
  and its content hash. A database whose model provisions schemas reports
  `SupportsSchemaProvisioning`, and `DatabaseInstance.ApplySchemaAsync` applies the schema and
  returns `SchemaMigrationResult`. SQL declarations and relational shapes live in
  `Database.Sql.Schema`; Hosting receives an already compiled schema through
  `AddDatabase(engine, name, schema)`.
- **Object ownership** — `DatabaseObjectOwner` distinguishes code-first provisioning from ad-hoc
  statements. Schema-owned objects require schema apply to change; ad-hoc objects remain mutable
  through session statements. `DatabaseObjectLockedException` identifies the refused object,
  owning schema, and operation.
- **Session contracts** — query execution (typed `QueryRequest` and language-text overloads) and
  transaction management. Sessions are single-threaded by contract; disposing one rolls back its
  active transaction.
- **Error root** — `DatabaseException` for the contract root and everything built above it;
  `DatabaseNotFoundException` for the exact missing-database outcome of
  `DatabaseEngine.OpenDatabaseAsync`; `DatabaseParseException` for statement text a session's
  language rejects; `DatabaseOfflineException` (with a model `Code`) for every operation on a
  database whose journal or data fsync (#1243), journal drain (#1252) or header slot write
  (#1268) failed, or whose engine gave up on it (owner decision 25), until it is reopened, and
  `DatabaseTransactionCommitUnconfirmedException` for work that may have committed when it did.
  `DatabaseEngine.OfflineDatabases` lists the offline databases for health, and
  `DatabaseEngine.GetOfflineError` says what took one offline. Child roots own independent
  exception roots (see [DESIGN.md](DESIGN.md)).

## Dependencies

`Core`, the eight child roots the root rolls up (`Database.Execution`, `Database.Indexing`,
`Database.Language`, `Database.Protocol`, `Database.Security`, `Database.Storage`,
`Database.Transactions`, `Database.Types` — child roots never reference the root), and the
existing private `Web` implementation reference (excluded from the package dependency list). The
HTTP `admin` control plane is a separate Hosting concern implemented through that project's
private `Web.Hosting`/`Web.Health` references.

## Consumers

Every `resources/Database/*` project. Model engines (`Database.Sql`, …) derive from the bases;
each model's server (`SqlDatabaseServer`, …) pumps wire-protocol frames into sessions;
`Database.Client` owns connection pooling and framed exchanges, while each model client
materializes results; `Database.Hosting` composes servers into a host.

See [DESIGN.md](DESIGN.md) for the contract-shape decisions.

Phase 29 gave each model an options-bearing engine builder with deferred worker/server factories
(sealed and typed over the model's engine since phase 4 of the concrete-types plan).
`DatabaseEngine.Servers` exposes nested servers for host lifecycle discovery; engines retain their
disposal ownership. Named engine operations use `DatabaseName`. `IDatabaseApplication` is
asynchronously disposable, its context includes all engines and ordinal `GetEngine(name)`, and its
builder exposes AddEngine(instance/factory) plus one-shot Build without a mutable registry or Use
stage.
