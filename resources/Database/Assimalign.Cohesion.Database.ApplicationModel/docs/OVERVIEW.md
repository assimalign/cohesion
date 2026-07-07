# Assimalign.Cohesion.Database.ApplicationModel — Overview

The orchestration manifest for the Cohesion database (ApplicationModel Layer 3d): `DatabaseResource` declares the database to a gateway as an executable resource with a wire-protocol endpoint and a persistent data volume; `AddDatabase(...)` composes it into an application graph.

```csharp
var builder = Application.CreateBuilder(args);
var db  = builder.AddDatabase("orders-db");
var api = builder.AddWebApp("orders-api").DependsOn(db);
builder.UseGateway(new LocalGateway());
await builder.Build().RunAsync();
```

## Scope

- `DatabaseResource` — `IExecutableResource` + `IEndpointResource` + `IMountResource` manifest
- `DatabaseResourceOptions` — port (0 = platform-allocated), data mount path, environment variables
- `AddDatabase(...)` builder extensions

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` — and nothing else. This project must never reference the database runtime (the manifest/runtime split is the ApplicationModel's core invariant).

## Consumers

Orchestrator applications. The database host itself never references this package.
