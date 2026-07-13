# Assimalign.Cohesion.Database.Hosting

## Summary

The standalone hosting application for the database engine resource: a `Host<TContext>`
subclass that composes the resource's units of work as hosted services on the
per-service execution menu, and the one DI/Configuration/Logging seam for the area.
The module also owns the **database server runtime** — the model-agnostic network
front-end that accepts connections over `libraries/Connections` drivers, authenticates
a session principal, binds the session to a database, and pumps wire-protocol frames
into engine `IDatabaseSession` executions (`Database.Server` was folded in on
2026-07-12; see `docs/DESIGN.md`).

## Current Evaluation

- Status: Composition + server runtime + engine-worker mapping delivered — the host drives engine lifecycle through the root contract, claims the engine-owned background workers (WAL group-commit flush, page write-back, checkpoint, maintenance) onto the execution menu (#902), runs the wire endpoint, and drains it all in order on stop.
- Project references: `Assimalign.Cohesion.Database` (area root), `Assimalign.Cohesion.Database.Protocol` + `Assimalign.Cohesion.Database.Security` (the server's own machinery, COHRES002-exempted via `CohesionHostingIsolationExemptions`), `Assimalign.Cohesion.Connections` (transport drivers), `Assimalign.Cohesion.Hosting` (non-area hosting foundation).

## Primary Responsibilities

- `DatabaseApplication` owns the resource process lifecycle (start, run, stop) via
  `Host<DatabaseApplicationContext>`, composing (in registration order) the engine
  lifecycle services, the claimed engine-worker slots, any additional host services,
  and the wire-protocol endpoint (registered last so it starts last and drains
  first). Worker slots schedule engine-owned `IDatabaseEngineWorker`s — the work
  itself lives in the engine, so embedded consumers get it without this module.
- The server runtime: the `IDatabaseServer` implementation (accept/drain lifecycle
  over the registered engines, created with `DatabaseServer.Create(options)`) and
  `DatabaseServerOptions` (engines, bound listener, authenticator, plus the DoS
  guardrails: session limit, auth/idle timeouts, drain budget). The abstractions
  themselves — `IDatabaseServer`, `IDatabaseServerSession`, `ProtocolVersion` — live
  in the area root so feature libraries can consume the seam without referencing
  this module. Statements execute through the root contract's
  text-execute seam (`IDatabaseSession.ExecuteAsync(string, parameters)`), so the
  server never parses any model language; parameters and result rows ride the shared
  tuple-codec (`DatabaseValueCodec` in `Database.Types`).
- `DatabaseApplicationContext` carries the environment, the served engines, and the
  composed hosted services.
- `DatabaseApplicationOptions` collects the engines, the server, additional
  `IHostService`s, and the worker mapping (`Workers`: per-kind enable + execution
  model; cadence lives on the engine's own options).
- `DatabaseHostConfiguration` binds the environment-variable conventions a gateway
  injects (data path, endpoint port, durability).

## Key Types

- `DatabaseApplication`
- `DatabaseApplicationContext`
- `DatabaseApplicationOptions`
- `DatabaseWorkerMappingOptions` / `DatabaseWorkerSlotOptions` / `DatabaseWorkerExecution`
- `DatabaseHostConfiguration`
- `DatabaseServer` (implements the root's `IDatabaseServer`)
- `DatabaseServerOptions`

## Composing a host

```csharp
var serverOptions = new DatabaseServerOptions { Listener = listener };
serverOptions.Engines.Add(engine);

var options = new DatabaseApplicationOptions();
options.Engines.Add(engine);
options.Server = DatabaseServer.Create(serverOptions);   // the wire endpoint

await using var app = new DatabaseApplication(options);
await app.RunAsync();   // starts engines, then the claimed worker slots, then the endpoint
```

A custom or embedded host composes `DatabaseServer.Create(...)` manually and drives
`IDatabaseServer.StartAsync`/`StopAsync` on its own lifecycle. `Database.Client` is the
counterpart on the other end of the wire.
