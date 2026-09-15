# VpnGateway

VpnGateway is the L3 networking service platform intended to manage tunnels, peers, key exchange policy, route advertisement, and VPN session lifecycle.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.VpnGateway` defines the public area-root application and builder contracts.
- `Assimalign.Cohesion.VpnGateway.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.

- `Assimalign.Cohesion.VpnGateway.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, VpnGateway composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.VpnGateway/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.VpnGateway/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.VpnGateway.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.VpnGateway.Hosting/docs/DESIGN.md)
