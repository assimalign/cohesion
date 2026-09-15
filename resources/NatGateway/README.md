# NatGateway

NatGateway is the L3 networking service platform intended to manage source and destination translation rules, live translation sessions, port allocation, and egress policy.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

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
