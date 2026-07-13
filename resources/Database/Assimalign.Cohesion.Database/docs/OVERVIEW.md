# Assimalign.Cohesion.Database — Overview

The contract root of the Cohesion Data Platform: the model-agnostic interfaces
every engine implements and every consumer programs against —
`IDatabaseEngine` (lifecycle of logical databases), `IDatabase` (a logical
database), `IDatabaseSession` (scoped execution context), and
`IDatabaseTransaction` (explicit ACID scope) — plus the server seam
(`IDatabaseServer`, `IDatabaseServerSession`; the runtime lives in
`Database.Hosting`), the area's exception root (`DatabaseException`,
`DatabaseParseException`), and shared value objects (`DatabaseName`, lifecycle
enums). The root is also the area's **rollup**: it references every child root
(Types, Language, Storage, Transactions, Execution, Protocol, Security,
Governance), so one reference to the root delivers the whole base surface —
including child-owned vocabulary the contracts speak (`TransactionId` and
`TransactionState` from `Database.Transactions`, `ProtocolVersion` from
`Database.Protocol`).

## Scope

- **Engine contracts** — create/open/drop/enumerate logical databases; engine
  lifecycle (`EngineState`) and model identity (`EngineModel`).
- **Session contracts** — query execution (typed `QueryRequest` and
  language-text overloads) and transaction management. Sessions are
  single-threaded by contract; disposing one rolls back its active transaction.
- **Error root** — `DatabaseException` for the contract root and everything
  built above it; `DatabaseParseException` for statement text a session's
  language rejects. Child roots own independent exception roots (see
  [DESIGN.md](DESIGN.md)).

## Dependencies

`Core`, the eight child roots the root rolls up (`Database.Execution`,
`Database.Governance`, `Database.Language`, `Database.Protocol`,
`Database.Security`, `Database.Storage`, `Database.Transactions`,
`Database.Types` — child roots never reference the root), and a private `Web`
reference (internal admin-surface concern, excluded from the package dependency
list).

## Consumers

Every `resources/Database/*` project. Model engines (`Database.Sql`, …)
implement the contracts; the server runtime in `Database.Hosting` pumps
wire-protocol frames into sessions; `Database.Client` mirrors results on the
caller side; `Database.Hosting` composes engines into a host.

See [DESIGN.md](DESIGN.md) for the contract-shape decisions.
