# MediaHub

MediaHub is the L3 media service platform intended to coordinate asset ingest and cataloging, processing jobs, packaging and manifests, and delivery policy.

The retired resource integration (control plane, health, telemetry) was removed on 2026-10-31 with the application-model restart (assimalign/cohesion#1759), and the area's resource integration returns as `Assimalign.Cohesion.MediaHub.Hosting.ApplicationModel`. Domain services remain fillers pending the area program.

## Project map

An arrow means "references": `MediaHub.Hosting --> MediaHub` reads
`MediaHub.Hosting` references `Assimalign.Cohesion.MediaHub`.

```mermaid
flowchart LR
    P0["MediaHub — area root"]
    P2["MediaHub.Hosting — runtime module"]
    CORE["Assimalign.Cohesion.Core — L1"]
    HOSTFAM["Assimalign.Cohesion.Hosting family — L2"]
    PRIV["other areas, referenced privately"]
    P0 --> CORE
    P2 --> HOSTFAM
    P2 --> P0
    P2 -->|"private"| PRIV
    P0 -.->|"COHRES001 ✗"| P2
```

Solid edges are the references this area permits. The dotted edge is the one `COHRES001`
rejects: **no library in the area may reference its own `MediaHub.Hosting` runtime
module**. The area root and its feature
libraries likewise reference no `Assimalign.Cohesion.Hosting*` library at all (`COHRES004`).

The full reference graph for every Cohesion assembly, including the exact external dependencies
collapsed above, is in [docs/DEPENDENCIES.md](../../docs/DEPENDENCIES.md).

## Projects

- `Assimalign.Cohesion.MediaHub` defines the public area-root application and builder contracts alongside the existing media-hub abstraction.
- `Assimalign.Cohesion.MediaHub.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.

## Layering and dependencies

As an L3 service platform, MediaHub composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation.

## Project documentation

- [Root overview](./Assimalign.Cohesion.MediaHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.MediaHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.MediaHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.MediaHub.Hosting/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `IMediaHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IMediaHubApplicationContext` exposes `ContentRootPath`. `IMediaHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`MediaHubApplication.CreateBuilder(args)` returns the public concrete `MediaHubApplicationBuilder`; its `Build()` returns the public `MediaHubApplication : Host<MediaHubApplicationContext>`. The public `MediaHubApplicationContext` implements `IMediaHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.MediaHub.Hosting;

MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using MediaHubApplication application = builder.Build();
await application.RunAsync();
```
