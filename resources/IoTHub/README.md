# IoTHub

IoTHub is the L3 service platform intended to provide device identity and provisioning, telemetry ingress, command dispatch, and device twin or shadow state.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.IoTHub` defines the public area-root application and builder contracts alongside the existing IoT-hub abstraction.
- `Assimalign.Cohesion.IoTHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

## Layering and dependencies

As an L3 service platform, IoTHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.Connections` library for connection-oriented primitives; the Hosting project depends only on the area root and shared Hosting runtime.

## Project documentation

- [Root overview](./Assimalign.Cohesion.IoTHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.IoTHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.IoTHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.IoTHub.Hosting/docs/DESIGN.md)
