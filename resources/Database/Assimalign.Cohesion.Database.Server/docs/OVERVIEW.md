# Assimalign.Cohesion.Database.Server — Overview

Shared server infrastructure for the Cohesion Data Platform: the guided abstract
base (`DatabaseServer`) every **per-model** wire-protocol server derives from,
plus its composition options (`DatabaseServerOptions`).

## What it provides

- **`DatabaseServer`** — the model-agnostic server machinery over exactly one
  engine: accept loop on the composed `IConnectionListener`, the session state
  machine and frame pump (startup → authenticate → ready ⇄ executing →
  terminated), the DoS guardrails (session limit, authentication timeout, idle
  eviction), and the two-phase graceful drain. Implements the area root's
  `IDatabaseServer`/`IDatabaseServerContext` seams.
- **`DatabaseServerOptions`** — the bound listener, the authenticator seam
  (`IDatabaseAuthenticator`, defaulting to `DatabaseAuthenticator.AllowAll`),
  and the guardrail knobs. Deliberately no engine: the derived server supplies
  its single engine.

## Who derives from it

Every model ships its own server in its model package — `SqlDatabaseServer` in
`Assimalign.Cohesion.Database.Sql` is the first — and that derived type is where
model-specific wire behavior grows. Composition roots create a model server
directly (`SqlDatabaseServer.Create(engine, options)`) or through the model's
builder verb (`AddSqlServer(...)`), then run it themselves
(`StartAsync`/`StopAsync`) or register it on a `DatabaseApplication`, which
wraps it as an endpoint host service (started last, drained first).

## Dependencies

- `Assimalign.Cohesion.Database` — the area root (rolls up the child roots, so
  `Database.Protocol` framing and `Database.Security`'s authenticator arrive
  transitively).
- `Assimalign.Cohesion.Connections` — transport listeners and connections.

This library sits *above* the area root and *below* the model packages; it is
deliberately not part of `Database.Hosting` (see docs/DESIGN.md).

## Documents

- [DESIGN.md](DESIGN.md) — design record: why the machinery lives here, the
  session state machine, error taxonomy, and drain semantics.
