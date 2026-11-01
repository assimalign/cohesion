# IoTHub

IoTHub is the L3 service platform intended to provide device identity and provisioning, telemetry ingress, command dispatch, and device twin or shadow state.

An IoTHub application becomes a managed resource instance with `Assimalign.Cohesion.IoTHub.Hosting.ApplicationModel`: `builder.Services.AddResource("devices", resource => resource.UseInProcess())` joins the gateway that runs it through the platform it opted into. Domain services remain fillers pending the area program.

## Project map

An arrow means "references": `IoTHub.Hosting --> IoTHub` reads
`IoTHub.Hosting` references `Assimalign.Cohesion.IoTHub`.

```mermaid
flowchart LR
    P0["IoTHub — area root"]
    P2["IoTHub.Hosting — runtime module"]
    PAM["IoTHub.Hosting.ApplicationModel — resource integration"]
    AMH["Assimalign.Cohesion.ApplicationModel.Hosting — L2"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    P2 --> HOSTFAM
    P2 --> P0
    PAM --> P2
    PAM --> P0
    PAM --> AMH
    AMH --> HOSTFAM
    P0 -.->|"COHRES001 ✗"| P2
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `IoTHub.Hosting` runtime
module**. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The one exception is the resource integration, `IoTHub.Hosting.ApplicationModel`, which joins
the runtime module to the application model's hosting; `COHRES001` sanctions that
reference by the integration's exact name, with no exemption property.

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.IoTHub` defines the public area-root application and builder contracts alongside the existing IoT-hub abstraction.
- `Assimalign.Cohesion.IoTHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.
- `Assimalign.Cohesion.IoTHub.Hosting.ApplicationModel` registers the application as a managed resource instance (`builder.Services.AddResource(name, ...)`) on the platform its gateway names, with the actions the application adds.

## Layering and dependencies

As an L3 service platform, IoTHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.Connections` library for connection-oriented primitives.

## Project documentation

- [Root overview](./Assimalign.Cohesion.IoTHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.IoTHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.IoTHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.IoTHub.Hosting/docs/DESIGN.md)
- [Resource integration overview](./Assimalign.Cohesion.IoTHub.Hosting.ApplicationModel/docs/OVERVIEW.md)
- [Resource integration design](./Assimalign.Cohesion.IoTHub.Hosting.ApplicationModel/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `IIoTHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIoTHubApplicationContext` exposes `ContentRootPath`. `IIoTHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IoTHubApplication.CreateBuilder(args)` returns the public concrete `IoTHubApplicationBuilder`; its `Build()` returns the public `IoTHubApplication : Host<IoTHubApplicationContext>`. The public `IoTHubApplicationContext` implements `IIoTHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.IoTHub.Hosting;

IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using IoTHubApplication application = builder.Build();
await application.RunAsync();
```
