# SecretStore

SecretStore is the L3 operational service platform intended to provide versioned secret persistence, access policy, leasing, rotation, auditing, trust, and certificate workflows.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.SecretStore` defines the public area-root application and builder contracts alongside the existing secret-store abstraction.
- `Assimalign.Cohesion.SecretStore.Hosting` provides the concrete creation entry point and the current empty host lifecycle.
- `Assimalign.Cohesion.SecretStore.Client` is the thin, Core-only HTTP protocol client used by gateways to read secret bytes and PEM certificates and to carry generic commands.

## Layering and dependencies

As an L3 service platform, SecretStore composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; the current area has no additional outside-area runtime dependency.

The client package is the narrow O13 orchestration exception: a gateway may reference it for
mount-source resolution and command delivery, but the client never references `*.Hosting` and is
not delivered through the `App.SecretStore` shared framework. Platform gateways continue to avoid
SecretStore ApplicationModel packages.

## Project documentation

- [Root overview](./Assimalign.Cohesion.SecretStore/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.SecretStore/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.SecretStore.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.SecretStore.Hosting/docs/DESIGN.md)
- [Client overview](./Assimalign.Cohesion.SecretStore.Client/docs/OVERVIEW.md)
- [Client design](./Assimalign.Cohesion.SecretStore.Client/docs/DESIGN.md)
