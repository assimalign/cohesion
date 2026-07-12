# Assimalign.Cohesion.Database — Overview

The contract root of the Cohesion Data Platform: the model-agnostic interfaces
every engine implements and every consumer programs against —
`IDatabaseEngine` (lifecycle of logical databases), `IDatabase` (a logical
database), `IDatabaseSession` (scoped execution context), and
`IDatabaseTransaction` (explicit ACID scope) — plus the area's exception root
(`DatabaseException`, `DatabaseParseException`) and shared value objects
(`DatabaseName`, `TransactionId`, lifecycle enums).

## Scope

- **Engine contracts** — create/open/drop/enumerate logical databases; engine
  lifecycle (`EngineState`) and model identity (`EngineModel`).
- **Session contracts** — query execution (typed `QueryRequest` and
  language-text overloads) and transaction management. Sessions are
  single-threaded by contract; disposing one rolls back its active transaction.
- **Error root** — `DatabaseException` for everything user-facing in the area;
  `DatabaseParseException` for statement text a session's language rejects.

## Dependencies

`Core`, `Database.Execution` (the abstract query request/result family the
session contract speaks), and a private `Web` reference (internal admin-surface
concern, excluded from the package dependency list).

## Consumers

Every `resources/Database/*` project. Model engines (`Database.Sql`, …)
implement the contracts; `Database.Server` pumps wire-protocol frames into
sessions; `Database.Client` mirrors results on the caller side;
`Database.Hosting` composes engines into a host.

See [DESIGN.md](DESIGN.md) for the contract-shape decisions.
