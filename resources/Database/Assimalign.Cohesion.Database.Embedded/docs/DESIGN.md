# Assimalign.Cohesion.Database.Embedded — Design

## Intent

The Database area is the data layer for the rest of the Cohesion platform (area `DESIGN.md` R10). Most platform resources should not run — or depend on — a separate database server process for their own state; they embed the engines they need. This project is that consumption path, and it is deliberately thin.

## The engine self-sufficiency principle

The facade can only be thin because of an invariant this project *enforces by existing*: **engines are self-sufficient libraries**. An engine owns its internal background workers — WAL flushing, checkpointing, version pruning — whether it runs embedded or hosted. `Database.Hosting` merely composes the per-model wire-protocol servers that front engines; it adds no behavior an embedded consumer would lose (since 2026-07-13 the engine's worker loops are engine-internal, always — there is no host scheduling to miss). If an engine ever requires the host to function, embedded consumers break — that is a design defect in the engine, not a missing feature here.

## Decisions

- **Composition only.** `EmbeddedDatabase` registers engines, looks them up by name or model, and disposes them in reverse registration order. It does not proxy engine operations — consumers work with `DatabaseEngine`/`DatabaseInstance` (or a model's sealed leaves, `SqlDatabaseEngine` and the rest) directly, so embedded and hosted code paths stay identical.
- **No DI.** Repo rule: `*.Hosting` is the only DI seam. Embedded consumers new up engines from their factories (`{Model}DatabaseEngine.Create(options)`); resources with DI wire this in their own hosting layer.
- **Engine-name uniqueness enforced at composition**, ordinal-ignore-case, matching `DatabaseEngine.Name` semantics elsewhere.
- **Typed over the root engine base (concrete-types plan, phase 6, #1262).** `EmbeddedDatabaseOptions.Engines`, `EmbeddedDatabase.Engines` and both `TryGetEngine` overloads name `DatabaseEngine`, the root base every model engine derives from; the `IDatabaseEngine` interface they named is deleted (plan §5.2). `TryGetEngine`'s engine is `[MaybeNullWhen(false)]`: null when the lookup fails. A caller that knows the model casts the engine it gets back to the model's sealed engine.
- **Best-effort disposal with aggregation.** One failing engine must not leak the others' file handles; failures are collected and rethrown as `AggregateException`.

## Non-goals

- No cross-engine transactions — a transaction is scoped to one database in one engine.
- No configuration binding here — `cohesion.config` binding belongs to the consuming resource's hosting layer.
- No lifecycle states beyond compose/dispose — engines own their own `EngineState`.

## AOT posture

Pure composition; no reflection, no discovery. Consumers reference engine packages statically.

## Phase 29 root-contract migration

The embedded test engine now implements the root's `DatabaseName` operations and
read-only `Servers` observation; since phase 6 of the concrete-types plan it derives from the
root `DatabaseEngine` base and implements only its protected cores, its disposal core recording
the disposal order (`database-area.md`, "Test doubles"). Embedded composition continues to own the engines
explicitly supplied to `EmbeddedDatabase`; this existing aggregate is separate
from the hosting builder's instance-borrowed registration contract.