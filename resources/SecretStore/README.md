# SecretStore

SecretStore is the L3 operational service platform intended to provide versioned secret persistence, access policy, leasing, rotation, auditing, trust, and certificate workflows.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.SecretStore` defines the public area-root application and builder contracts alongside the existing secret-store abstraction.
- `Assimalign.Cohesion.SecretStore.Hosting` provides the concrete creation entry point and the current empty host lifecycle.
- `Assimalign.Cohesion.SecretStore.Client` contains the existing client-side contract surface and remains an early implementation.

## Layering and dependencies

As an L3 service platform, SecretStore composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; the current area has no additional outside-area runtime dependency.

## Project documentation

- [Root overview](./Assimalign.Cohesion.SecretStore/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.SecretStore/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.SecretStore.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.SecretStore.Hosting/docs/DESIGN.md)
