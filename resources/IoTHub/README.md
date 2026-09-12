# IoTHub

IoTHub is the L3 service platform intended to provide device identity and provisioning, telemetry ingress, command dispatch, and device twin or shadow state.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.IoTHub` defines the public area-root application and builder contracts alongside the existing IoT-hub abstraction.
- `Assimalign.Cohesion.IoTHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

- `Assimalign.Cohesion.IoTHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, IoTHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.Connections` library for connection-oriented primitives; Hosting additionally consumes the resource/health contracts and privately composes the Web control-plane listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.IoTHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.IoTHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.IoTHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.IoTHub.Hosting/docs/DESIGN.md)
