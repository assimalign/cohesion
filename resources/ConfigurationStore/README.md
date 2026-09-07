# ConfigurationStore

ConfigurationStore is the L3 service platform intended to manage configuration namespaces, versioned snapshots, revisions, watches, and promotion policy.

The application and builder are fillers pending the area's program.

## Projects

- `Assimalign.Cohesion.ConfigurationStore` defines the public area-root application and builder contracts alongside the existing loader abstraction.
- `Assimalign.Cohesion.ConfigurationStore.Hosting` provides the concrete creation entry point and the current empty host lifecycle.

## Layering and dependencies

As an L3 service platform, ConfigurationStore composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; the current area has no additional outside-area runtime dependency.

## Project documentation

- [Root overview](./Assimalign.Cohesion.ConfigurationStore/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.ConfigurationStore/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.ConfigurationStore.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.ConfigurationStore.Hosting/docs/DESIGN.md)
