# ApiManager

ApiManager is the L3 service platform intended to manage API backends, routes, products, subscriptions, contracts, and gateway policy execution.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.ApiManager` defines the public area-root application and builder contracts.
- `Assimalign.Cohesion.ApiManager.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

- `Assimalign.Cohesion.ApiManager.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, ApiManager composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.ControlPlane, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.ApiManager/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.ApiManager/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.ApiManager.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.ApiManager.Hosting/docs/DESIGN.md)
