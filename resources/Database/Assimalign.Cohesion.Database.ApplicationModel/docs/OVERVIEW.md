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
- `DatabaseResourceControlPlane` — the default control-plane factory registered by enabled database resources

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` for the declarative resource model
- `Assimalign.Cohesion.Hosting` for the Core-only control-plane contract

The project never references `Database.Hosting`. The generated registration and
the runtime meet through `ResourceRuntime`, preserving the manifest/runtime
split while allowing every enabled database resource to expose the same default
control plane on its ambient `admin` endpoint.

## Consumers

Gateways consume the typed resource model. An SDK-enabled database executable
also consumes the default control-plane factory through generated code; the
database runtime itself never references this package.
