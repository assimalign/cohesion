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
