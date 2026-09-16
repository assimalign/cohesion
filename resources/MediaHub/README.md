# MediaHub

MediaHub is the L3 media service platform intended to coordinate asset ingest and cataloging, processing jobs, packaging and manifests, and delivery policy.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.MediaHub` defines the public area-root application and builder contracts alongside the existing media-hub abstraction.
- `Assimalign.Cohesion.MediaHub.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.

- `Assimalign.Cohesion.MediaHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, MediaHub composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

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
