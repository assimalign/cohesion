# EmailHub

EmailHub is the L3 service platform intended to compose email messages and templates, select transports, track delivery, and apply suppression policy.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.EmailHub` defines the public area-root application and builder contracts alongside the existing email-hub abstraction.
- `Assimalign.Cohesion.EmailHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

- `Assimalign.Cohesion.EmailHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, EmailHub composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.EmailHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.EmailHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.EmailHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.EmailHub.Hosting/docs/DESIGN.md)
