# Assimalign.Cohesion.Database.Server — Overview

The shared database server core: `DatabaseServer`, the guided base every
per-model wire-protocol server derives from, and `DatabaseServerOptions`, the
common composition surface (listener, authenticator, DoS guardrails).

## Purpose

Owns the **proven** model-agnostic server machinery — accept loop, session
table, the session state machine and frame pump, authentication/idle/
session-limit guardrails, and the two-phase graceful drain — so per-model
servers (`SqlDatabaseServer` in `Database.Sql`, `KeyValueDatabaseServer` in
`Database.KeyValuePair`) stay thin derivations that name their engine type and
options subtype. Extracted from `Database.Sql`'s internal machinery on
2026-07-14, when the second model server fired the area's recorded extraction
trigger; the prediction-vs-evidence record is in [DESIGN.md](DESIGN.md).

## Scope

- `DatabaseServer` — the guided abstract base implementing the area root's
  `IDatabaseServer` contract.
- `DatabaseServerOptions` — the common options base model options derive from.
- Nothing else: model servers, their factories, and their builder verbs live in
  the model packages; the root contracts stay the only area-wide requirement.

## Dependencies

- `Assimalign.Cohesion.Database` — the area root (contracts; Protocol, Security,
  Execution, and Types arrive transitively through its child-root rollup).
- `Assimalign.Cohesion.Connections` — the transport drivers the accept loop
  consumes (the composition root binds and owns the listener).

## Usage

Never composed directly — a model package derives:

```csharp
public sealed class KeyValueDatabaseServer : DatabaseServer
{
    private KeyValueDatabaseServer(KeyValueDatabaseEngine engine, KeyValueDatabaseServerOptions options)
        : base(engine, options) { … }
}
```

Applications compose model servers through their model's builder verb
(`AddSqlServer(...)`, `AddKeyValueServer(...)`) or `Create` factory.
