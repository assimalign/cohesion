# Assimalign.Cohesion.Database.Hosting

## Summary

The standalone hosting application for the database engine resource: a `Host<TContext>`
subclass that composes the resource's units of work as hosted services on the
per-service execution menu, and the one DI/Configuration/Logging seam for the area.

## Current Evaluation

- Status: Composition delivered — the host runs the wire endpoint and drains it on stop; durability worker slots are documented placeholders pending the engine's background-worker seam.
- Project references: `Assimalign.Cohesion.Database` (area root), `Assimalign.Cohesion.Hosting` (non-area hosting foundation).

## Primary Responsibilities

- `DatabaseApplication` owns the resource process lifecycle (start, run, stop) via
  `Host<DatabaseApplicationContext>`, composing the durability worker slots and the
  composition root's endpoint (and other) host services.
- `DatabaseApplicationContext` carries the environment, the served engines, and the
  composed hosted services.
- `DatabaseApplicationOptions` collects the engines, the endpoint/other `IHostService`s,
  and the durability-slot toggles.
- `DatabaseHostConfiguration` binds the environment-variable conventions a gateway
  injects (data path, endpoint port, durability).

## Key Types

- `DatabaseApplication`
- `DatabaseApplicationContext`
- `DatabaseApplicationOptions`
- `DatabaseHostConfiguration`

## Composing a host

```csharp
// The composition root references Database.Server (the hosting module cannot).
var server = DatabaseServer.Create(new DatabaseServerOptions { Listener = listener, /* Engines = ... */ });

var options = new DatabaseApplicationOptions();
options.Engines.Add(engine);
options.Services.Add(DatabaseServer.CreateHostService(server));   // the endpoint host service

await using var app = new DatabaseApplication(options);
await app.RunAsync();   // starts durability slots + endpoint, drains on shutdown
```

See `docs/DESIGN.md` for why the endpoint host service is composed rather than owned.
