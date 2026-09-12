# LogSpace

LogSpace is the L3 observability service platform intended to provide append-only log ingestion, indexing, retention, archival, query, correlation lookup, and export.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.LogSpace` defines the public area-root application and builder contracts.
- `Assimalign.Cohesion.LogSpace.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.
- `Assimalign.Cohesion.LogSpace.Telemetry` reserves the area-specific telemetry integration surface and is currently project scaffolding.

- `Assimalign.Cohesion.LogSpace.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, LogSpace composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.ControlPlane, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.LogSpace/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.LogSpace/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.LogSpace.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.LogSpace.Hosting/docs/DESIGN.md)
