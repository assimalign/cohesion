# Assimalign.Cohesion.Database — Overview

The contract root of the Cohesion Data Platform: the model-agnostic interfaces
every engine implements and every consumer programs against —
`IDatabaseEngine` (a data machine managing logical databases), `IDatabase` (a
logical database), `IDatabaseSession` (scoped execution context), and
`IDatabaseTransaction` (explicit ACID scope) — plus the server seam
(`IDatabaseServer`, `IDatabaseServerContext`, `IDatabaseServerSession`; servers
are per-model, each implemented inside its model package),
the application seam (`IDatabaseApplication`, `IDatabaseApplicationContext`,
`IDatabaseApplicationBuilder`), the area's exception root (`DatabaseException`,
`DatabaseParseException`), and shared value objects (`DatabaseName`,
`EngineState`, `EngineModel`). The root is also the area's **rollup**: it
references every child root (Types, Language, Storage, Transactions, Execution,
Indexing, Protocol, Security, Governance), so one reference to the root delivers
the whole base surface — including child-owned vocabulary the contracts speak
(`TransactionId` and `TransactionState` from `Database.Transactions`,
`ProtocolVersion` from `Database.Protocol`).

## Scope

- **Engine contracts** — create/open/drop/enumerate logical databases. Engines
  are **data machines**: operational from creation, no start/stop ceremony;
  disposal quiesces their background workers and durably flushes. `State` is
  observational (`Running`/`Faulted`/`Disposed`); `Workers` exposes the
  engine-owned background loops for diagnostics (name, kind, cadence).
- **Server contracts** — `IDatabaseServer` (start/stop lifecycle — "running"
  lives on the server, never the engine) with its observational
  `IDatabaseServerContext` (the one engine it fronts + active sessions).
  Servers are per-model and these contracts are the only area-wide
  requirement — each model implements them inside its model package
  (`SqlDatabaseServer` in `Database.Sql`).
- **Application composition seam** — `IDatabaseApplicationBuilder` /
  `IDatabaseApplication` / `IDatabaseApplicationContext`: model packages
  register their engines and servers against this root seam (e.g.
  `Database.Sql`'s `AddSqlDatabase(...)` / `AddSqlServer(...)` verbs) without
  knowing the hosting layer; `Database.Hosting` implements it
  (`DatabaseApplication.CreateBuilder()`).
- **Session contracts** — query execution (typed `QueryRequest` and
  language-text overloads) and transaction management. Sessions are
  single-threaded by contract; disposing one rolls back its active transaction.
- **Error root** — `DatabaseException` for the contract root and everything
  built above it; `DatabaseParseException` for statement text a session's
  language rejects. Child roots own independent exception roots (see
  [DESIGN.md](DESIGN.md)).

## Dependencies

`Core`, the nine child roots the root rolls up (`Database.Execution`,
`Database.Governance`, `Database.Indexing`, `Database.Language`,
`Database.Protocol`, `Database.Security`, `Database.Storage`,
`Database.Transactions`, `Database.Types` — child roots never reference the
root), and a private `Web` reference (internal admin-surface concern, excluded
from the package dependency list).

## Consumers

Every `resources/Database/*` project. Model engines (`Database.Sql`, …)
implement the contracts; each model's server (`SqlDatabaseServer`, …) pumps
wire-protocol frames into sessions; `Database.Client` mirrors results on the
caller side; `Database.Hosting` composes servers into a host.

See [DESIGN.md](DESIGN.md) for the contract-shape decisions.
