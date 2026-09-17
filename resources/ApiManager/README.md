# ApiManager

ApiManager is the L3 service platform intended to manage API backends, routes, products, subscriptions, contracts, and gateway policy execution.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Project map

An arrow means "references": `ApiManager.Hosting --> ApiManager` reads
`ApiManager.Hosting` references `Assimalign.Cohesion.ApiManager`.

```mermaid
flowchart LR
    P0["ApiManager — area root"]
    P1["ApiManager.ApplicationModel"]
    P2["ApiManager.Hosting — runtime module"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    APPMODEL["Assimalign.Cohesion.ApplicationModel — L2"]
    PRIV["other areas, referenced privately"]
    P0 --> CORE
    P1 --> APPMODEL
    P1 --> HOSTFAM
    P2 --> P0
    P2 --> HOSTFAM
    P2 -->|"private"| PRIV
    P1 -.->|"COHRES001 ✗"| P2
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `ApiManager.Hosting` runtime
module**, and the declarative `.ApplicationModel` package in particular never does — generated
code in an opted-in consumer executable joins the two sides at run time through
`Assimalign.Cohesion.Hosting.Resources.ResourceRuntime` instead. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.ApiManager` defines the public area-root application and builder contracts.
- `Assimalign.Cohesion.ApiManager.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

- `Assimalign.Cohesion.ApiManager.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, ApiManager composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.ApiManager/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.ApiManager/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.ApiManager.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.ApiManager.Hosting/docs/DESIGN.md)

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
