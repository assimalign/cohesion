# Assimalign.Cohesion.Database.Embedded — Design

## Intent

The Database area is the data layer for the rest of the Cohesion platform (area `DESIGN.md` R10). Most platform resources should not run — or depend on — a separate database server process for their own state; they embed the engines they need. This project is that consumption path, and it is deliberately thin.

## The engine self-sufficiency principle

The facade can only be thin because of an invariant this project *enforces by existing*: **engines are self-sufficient libraries**. An engine owns its internal background workers — WAL flushing, checkpointing, version pruning — whether it runs embedded or hosted. `Database.Hosting` merely *composes* engines with the wire-protocol server and maps their workers onto the host's execution menu; it adds no behavior an embedded consumer would lose. If an engine ever requires the host to function, embedded consumers break — that is a design defect in the engine, not a missing feature here.

## Decisions

- **Composition only.** `EmbeddedDatabase` registers engines, looks them up by name or model, and disposes them in reverse registration order. It does not proxy engine operations — consumers work with `IDatabaseEngine`/`IDatabase` directly, so embedded and hosted code paths stay identical.
- **No DI.** Repo rule: `*.Hosting` is the only DI seam. Embedded consumers new up engines from their factories (`{Model}DatabaseEngine.Create(options)`); resources with DI wire this in their own hosting layer.
- **Engine-name uniqueness enforced at composition**, ordinal-ignore-case, matching `IDatabaseEngine.Name` semantics elsewhere.
- **Best-effort disposal with aggregation.** One failing engine must not leak the others' file handles; failures are collected and rethrown as `AggregateException`.

## Non-goals

- No cross-engine transactions — a transaction is scoped to one database in one engine.
- No configuration binding here — `cohesion.config` binding belongs to the consuming resource's hosting layer.
- No lifecycle states beyond compose/dispose — engines own their own `EngineState`.

## AOT posture

Pure composition; no reflection, no discovery. Consumers reference engine packages statically.
