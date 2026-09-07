# IdentityHub

IdentityHub is the L3 identity service platform intended to manage tenants, directories, applications, principals, credentials, sessions, token services, federation, and provisioning.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.IdentityHub` defines the public area-root application and builder contracts alongside the existing identity domain contracts.
- `Assimalign.Cohesion.IdentityHub.Hosting` provides the concrete creation entry point and the current empty host lifecycle.
- `Assimalign.Cohesion.IdentityHub.Models` contains the existing tenant, user, group, application, role, credential, and service-principal model types.

## Layering and dependencies

As an L3 service platform, IdentityHub composes the L2 `Assimalign.Cohesion.Hosting` runtime, which is built on the L1 `Assimalign.Cohesion.Core` foundation. The root project also depends on the L1 `Assimalign.Cohesion.IdentityModel` contracts, while the Models project references Core directly; the Hosting project depends only on the area root and shared Hosting runtime.

## Project documentation

- [Root overview](./Assimalign.Cohesion.IdentityHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.IdentityHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.IdentityHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.IdentityHub.Hosting/docs/DESIGN.md)
