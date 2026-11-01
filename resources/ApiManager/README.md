# ApiManager

ApiManager is the L3 service platform intended to manage API backends, routes, products, subscriptions, contracts, and gateway policy execution.

An ApiManager application becomes a managed resource instance with `Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel`: `builder.Services.AddResource("apis", resource => resource.UseInProcess())` joins the gateway that runs it through the platform it opted into. Domain services remain fillers pending the area program.

## Project map

An arrow means "references": `ApiManager.Hosting --> ApiManager` reads
`ApiManager.Hosting` references `Assimalign.Cohesion.ApiManager`.

```mermaid
flowchart LR
    P0["ApiManager — area root"]
    P2["ApiManager.Hosting — runtime module"]
    PAM["ApiManager.Hosting.ApplicationModel — resource integration"]
    AMH["Assimalign.Cohesion.ApplicationModel.Hosting — L2"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    P0 --> CORE
    P2 --> P0
    P2 --> HOSTFAM
    PAM --> P2
    PAM --> P0
    PAM --> AMH
    AMH --> HOSTFAM
    P0 -.->|"COHRES001 ✗"| P2
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `ApiManager.Hosting` runtime
module**. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The one exception is the resource integration, `ApiManager.Hosting.ApplicationModel`, which joins
the runtime module to the application model's hosting; `COHRES001` sanctions that
reference by the integration's exact name, with no exemption property.

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.ApiManager` defines the public area-root application and builder contracts.
- `Assimalign.Cohesion.ApiManager.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.
- `Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel` registers the application as a managed resource instance (`builder.Services.AddResource(name, ...)`) on the platform its gateway names, with the actions the application adds.

## Layering and dependencies

As an L3 service platform, ApiManager composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation.

## Project documentation

- [Root overview](./Assimalign.Cohesion.ApiManager/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.ApiManager/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.ApiManager.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.ApiManager.Hosting/docs/DESIGN.md)
- [Resource integration overview](./Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel/docs/OVERVIEW.md)
- [Resource integration design](./Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `IApiManagerApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IApiManagerApplicationContext` exposes `ContentRootPath`. `IApiManagerApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`ApiManagerApplication.CreateBuilder(args)` returns the public concrete `ApiManagerApplicationBuilder`; its `Build()` returns the public `ApiManagerApplication : Host<ApiManagerApplicationContext>`. The public `ApiManagerApplicationContext` implements `IApiManagerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.ApiManager.Hosting;

ApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using ApiManagerApplication application = builder.Build();
await application.RunAsync();
```
