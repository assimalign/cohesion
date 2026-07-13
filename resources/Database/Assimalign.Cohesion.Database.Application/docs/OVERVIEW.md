# Assimalign.Cohesion.Database.Application

## Summary

The standalone database host **executable** — the artifact the `DatabaseResource`
orchestration manifest declares (`Assimalign.Cohesion.Database.Application`). It is
the composition root for the out-of-process database resource: it binds the
conventional environment variables, composes a file-backed SQL engine (a data
machine, operational from creation), the SQL model's wire-protocol server
(`SqlDatabaseServer`) over a TCP listener, and the `DatabaseApplication` host
that runs the server, and runs until the shutdown signal drains it gracefully.

## Current Evaluation

- Status: Delivered (#904) — the executable serves the SQL model over TCP with real
  durability; other model engines join the composition as they ship.
- Project references: `Assimalign.Cohesion.Database` (area root),
  `Assimalign.Cohesion.Database.Hosting` (**sanctioned COHRES001 exemption** — the
  composition-root executable is the analog of a user application),
  `Assimalign.Cohesion.Database.Sql` + `Assimalign.Cohesion.Database.Storage`
  (the engine it composes and its durability options), and
  `Assimalign.Cohesion.Connections.Tcp` (the transport driver).

## Primary Responsibilities

- Bind `COHESION_DATABASE_DATA_PATH` / `COHESION_DATABASE_ENDPOINT_PORT` /
  `COHESION_DATABASE_DURABILITY` via `DatabaseHostConfiguration.FromEnvironment()`
  and reject malformed values loudly (non-zero exit).
- Compose the stack (`DatabaseApplicationBootstrap`): file-backed `SqlDatabaseEngine`
  at the data path (in-memory when unset), durability mode mapped from the
  convention (`full`/`synchronous` → per-commit fsync; `grouped`/`relaxed` → the
  group-commit window), a `TcpConnectionListener` on all interfaces at the
  configured port (OS-assigned when unset), `SqlDatabaseServer`, and
  `DatabaseApplication`.
- Run until SIGTERM/Ctrl+C, then drain: the endpoint drains first; disposing the
  composition then disposes the engine — workers quiesce and the data flushes
  durably.

## Key Types

- `Program` — the thin shim (`Main`).
- `DatabaseApplicationBootstrap` (internal) — env-config → composition mapping,
  unit-testable without spawning a process.
- `DatabaseApplicationComposition` (internal) — the composed parts owned as one
  disposable unit.

## Running

```
COHESION_DATABASE_DATA_PATH=/var/lib/cohesion-db \
COHESION_DATABASE_ENDPOINT_PORT=5999 \
COHESION_DATABASE_DURABILITY=grouped \
./Assimalign.Cohesion.Database.Application
```

Connect with `Database.Sql.Client` (`SqlClient`) or any `Database.Client` over the
wire protocol. The executable ships as a deployment artifact — it is deliberately
**not** part of the `App.Database` shared framework (frameworks deliver libraries).
