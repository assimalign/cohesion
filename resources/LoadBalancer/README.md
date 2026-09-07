# LoadBalancer

LoadBalancer is the L3 networking service platform intended to manage backend pools, health probes, routing and affinity policy, draining, failover, and load-balancing decisions.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.LoadBalancer` defines the public area-root application and builder contracts.
- `Assimalign.Cohesion.LoadBalancer.Hosting` provides the concrete creation entry point and the current empty host lifecycle.

## Layering and dependencies

As an L3 service platform, LoadBalancer composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.Http` library for HTTP-facing primitives; the Hosting project depends only on the area root and shared Hosting runtime.

## Project documentation

- [Root overview](./Assimalign.Cohesion.LoadBalancer/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.LoadBalancer/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.LoadBalancer.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.LoadBalancer.Hosting/docs/DESIGN.md)
