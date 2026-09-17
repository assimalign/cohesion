# NatGateway

NatGateway is the L3 networking service platform intended to manage source and destination translation rules, live translation sessions, port allocation, and egress policy.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Project map

An arrow means "references": `NatGateway.Hosting --> NatGateway` reads
`NatGateway.Hosting` references `Assimalign.Cohesion.NatGateway`.

```mermaid
flowchart LR
    P0["NatGateway — area root"]
    P1["NatGateway.ApplicationModel"]
    P2["NatGateway.Hosting — runtime module"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    APPMODEL["Assimalign.Cohesion.ApplicationModel — L2"]
    PRIV["other areas, referenced privately"]
    P0 --> CORE
    P1 --> APPMODEL
    P1 --> HOSTFAM
    P2 --> HOSTFAM
    P2 --> P0
    P2 -->|"private"| PRIV
    P1 -.->|"COHRES001 ✗"| P2
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `NatGateway.Hosting` runtime
module**, and the declarative `.ApplicationModel` package in particular never does — generated
code in an opted-in consumer executable joins the two sides at run time through
`Assimalign.Cohesion.Hosting.Resources.ResourceRuntime` instead. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.NatGateway` defines the public area-root application and builder contracts alongside the existing NAT-gateway abstraction.
- `Assimalign.Cohesion.NatGateway.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.

- `Assimalign.Cohesion.NatGateway.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, NatGateway composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.NatGateway/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.NatGateway/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.NatGateway.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.NatGateway.Hosting/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `INatGatewayApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `INatGatewayApplicationContext` exposes `ContentRootPath`. `INatGatewayApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`NatGatewayApplication.CreateBuilder(args)` returns the public concrete `NatGatewayApplicationBuilder`; its `Build()` returns the public `NatGatewayApplication : Host<NatGatewayApplicationContext>`. The public `NatGatewayApplicationContext` implements `INatGatewayApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.NatGateway.Hosting;

NatGatewayApplicationBuilder builder = NatGatewayApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using NatGatewayApplication application = builder.Build();
await application.RunAsync();
```
