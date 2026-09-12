# Rezolvr

Rezolvr is the L3 networking service platform intended to be a standalone DNS server with authoritative zones, forwarding and recursive resolution, caching, transfers, and administration.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.Rezolvr` defines the public area-root application and builder contracts for the standalone server product.
- `Assimalign.Cohesion.Rezolvr.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.

- `Assimalign.Cohesion.Rezolvr.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, Rezolvr composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.ControlPlane, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.Rezolvr/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.Rezolvr/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.Rezolvr.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.Rezolvr.Hosting/docs/DESIGN.md)
