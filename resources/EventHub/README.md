# EventHub

EventHub is the L3 service platform intended to provide partitioned event streams, publishing and consumption, consumer groups, checkpoints, retention, and replay.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.EventHub` defines the public area-root application and builder contracts alongside the existing event-hub abstraction.
- `Assimalign.Cohesion.EventHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

## Layering and dependencies

As an L3 service platform, EventHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.Connections` library for connection-oriented primitives; the Hosting project depends only on the area root and shared Hosting runtime.

## Project documentation

- [Root overview](./Assimalign.Cohesion.EventHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.EventHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.EventHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.EventHub.Hosting/docs/DESIGN.md)
