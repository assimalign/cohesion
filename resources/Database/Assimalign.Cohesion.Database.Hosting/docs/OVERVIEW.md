# Assimalign.Cohesion.Database.Hosting

## Summary

The standalone hosting application for the database resource: a `Host<TContext>`
subclass that composes the resource's units of work as hosted services on the
per-service execution menu, and the one DI/Configuration/Logging seam for the
area. Composition-only: it wraps the composed per-model wire-protocol servers
(`IDatabaseServer`) generically as endpoint host services — the server machinery
itself lives in `Assimalign.Cohesion.Database.Server`, and engines are
self-sufficient data machines this module never drives (see `docs/DESIGN.md`).

## Current Evaluation

- Status: Composition delivered on the redesigned shape (2026-07-13) — the
  application runs the composition root's services, then the registered servers
  (started last, drained first), and implements the root's application-builder
  seam. Engines take no part in the host lifecycle; workers are engine-internal.
- Project references: `Assimalign.Cohesion.Database` (area root) and
  `Assimalign.Cohesion.Hosting` (non-area hosting foundation). Nothing else — no
  `Connections`, no `CohesionHostingIsolationExemptions`.

## Primary Responsibilities

- `DatabaseApplication` owns the resource process lifecycle (start, run, stop)
  via `Host<DatabaseApplicationContext>`, composing (in registration order) the
  composition root's additional host services, then one endpoint service per
  registered server — servers start last and drain first.
- `DatabaseApplicationContext` implements the root's
  `IDatabaseApplicationContext`: the registered servers (plural — one per model)
  and the server-less engine registrations.
- `DatabaseApplicationOptions` collects the servers, the embedded engine
  registrations, and additional `IHostService`s.
- `DatabaseHostConfiguration` binds the environment-variable conventions a
  gateway injects (data path, endpoint port, durability).

## Key Types

- `DatabaseApplication` (implements the root's `IDatabaseApplication`; `CreateBuilder()` is the composition entry point)
- `DatabaseApplicationBuilder` (implements the root's `IDatabaseApplicationBuilder`)
- `DatabaseApplicationContext` (implements the root's `IDatabaseApplicationContext`)
- `DatabaseApplicationOptions`
- `DatabaseHostConfiguration`

## Composing a host

Builder-first — model packages register their engines *and servers* through the
root's `IDatabaseApplicationBuilder` seam (verbs ship with the model package,
e.g. `AddSqlDatabase` / `AddSqlServer` in `Database.Sql`):

```csharp
var builder = DatabaseApplication.CreateBuilder();

SqlDatabaseEngine engine = builder.AddSqlDatabase(options => options.RootPath = dataPath);
SqlDatabaseServer server = builder.AddSqlServer(engine, options => options.Listener = listener);

await using var app = builder.Build();
await app.RunAsync();   // starts services, then the servers (the engine is already live)
```

Hosting-only composition (additional host services) lives on `builder.Options`;
constructing `new DatabaseApplication(options)` directly from fully populated
options remains supported. A custom or embedded host creates a model server
(`SqlDatabaseServer.Create(engine, options)`) and drives
`IDatabaseServer.StartAsync`/`StopAsync` on its own lifecycle — or skips servers
entirely and uses the engine in-process. `Database.Client` is the counterpart on
the other end of the wire.
