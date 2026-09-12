# EventHub

EventHub is the L3 service platform intended to provide partitioned event streams, publishing and consumption, consumer groups, checkpoints, retention, and replay.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.EventHub` defines the public area-root application and builder contracts alongside the existing event-hub abstraction.
- `Assimalign.Cohesion.EventHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

- `Assimalign.Cohesion.EventHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, EventHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.Connections` library for connection-oriented primitives; Hosting additionally consumes the resource/health contracts and privately composes the Web control-plane listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.EventHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.EventHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.EventHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.EventHub.Hosting/docs/DESIGN.md)
