# NotificationHub

NotificationHub is the L3 messaging service platform intended to manage subscriptions and audiences, templates, channel routing, delivery policy, receipts, retries, and suppression.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Project map

An arrow means "references": `NotificationHub.Hosting --> NotificationHub` reads
`NotificationHub.Hosting` references `Assimalign.Cohesion.NotificationHub`.

```mermaid
flowchart LR
    P0["NotificationHub — area root"]
    P1["NotificationHub.ApplicationModel"]
    P2["NotificationHub.Client"]
    P3["NotificationHub.Hosting — runtime module"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    APPMODEL["Assimalign.Cohesion.ApplicationModel — L2"]
    PRIV["other areas, referenced privately"]
    P0 --> CORE
    P1 --> APPMODEL
    P1 --> HOSTFAM
    P3 --> HOSTFAM
    P3 --> P0
    P3 -->|"private"| PRIV
    P1 -.->|"COHRES001 ✗"| P3
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `NotificationHub.Hosting` runtime
module**, and the declarative `.ApplicationModel` package in particular never does — generated
code in an opted-in consumer executable joins the two sides at run time through
`Assimalign.Cohesion.Hosting.Resources.ResourceRuntime` instead. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.NotificationHub` defines the public area-root application and builder contracts alongside the existing notification-hub abstraction.
- `Assimalign.Cohesion.NotificationHub.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.
- `Assimalign.Cohesion.NotificationHub.Client` contains the existing client-side contract surface and remains an early implementation.

- `Assimalign.Cohesion.NotificationHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, NotificationHub composes the L2 `Assimalign.Cohesion.Hosting` runtime and also references the L1 `Assimalign.Cohesion.Core` foundation directly. Hosting consumes the area root and resource/health contracts publicly, and composes the Web control-plane listener privately.

## Project documentation

- [Root overview](./Assimalign.Cohesion.NotificationHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.NotificationHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.NotificationHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.NotificationHub.Hosting/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `INotificationHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `INotificationHubApplicationContext` exposes `ContentRootPath`. `INotificationHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`NotificationHubApplication.CreateBuilder(args)` returns the public concrete `NotificationHubApplicationBuilder`; its `Build()` returns the public `NotificationHubApplication : Host<NotificationHubApplicationContext>`. The public `NotificationHubApplicationContext` implements `INotificationHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.NotificationHub.Hosting;

NotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using NotificationHubApplication application = builder.Build();
await application.RunAsync();
```
