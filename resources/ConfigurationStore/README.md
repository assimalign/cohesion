# ConfigurationStore

ConfigurationStore is the L3 resource area for durable, named configuration namespaces. Its
default host serves namespace snapshots and declarative mutations over the resource control-plane
endpoint; values are configuration rather than secrets and are stored as plain JSON beneath the
`data` volume.

## Projects

- `Assimalign.Cohesion.ConfigurationStore` defines the public area-root application and builder contracts alongside the existing loader abstraction.
- `Assimalign.Cohesion.ConfigurationStore.ApplicationModel` supplies the manifest-backed typed resource, strict StatefulSet planner, and default control plane.
- `Assimalign.Cohesion.ConfigurationStore.Client` is the thin, Core-only HTTP protocol client used by gateways to read configuration namespace snapshots and carry generic commands.
- `Assimalign.Cohesion.ConfigurationStore.Hosting` provides the concrete creation entry point, durable store, ES256 bootstrap verification, and HTTP protocol host.

## Layering and dependencies

As an L3 service platform, ConfigurationStore composes the shared Hosting, Hosting.Resources,
IdentityModel, and private Web transport primitives rather than defining a second lifecycle or HTTP
stack. The ApplicationModel package remains orchestration-only and COHAM001-guarded.

The client package is the narrow O13 orchestration exception: a gateway may reference it for
mount-source resolution and command delivery, but the client never references `*.Hosting` and is
not delivered through the `App.ConfigurationStore` shared framework. Gateway platform
implementation assemblies continue to avoid ConfigurationStore ApplicationModel packages;
generated application gateway projects consume the area package for its typed resource and planner.

## Project documentation

- [Root overview](./Assimalign.Cohesion.ConfigurationStore/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.ConfigurationStore/docs/DESIGN.md)
- [ApplicationModel overview](./Assimalign.Cohesion.ConfigurationStore.ApplicationModel/docs/OVERVIEW.md)
- [ApplicationModel design](./Assimalign.Cohesion.ConfigurationStore.ApplicationModel/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.ConfigurationStore.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.ConfigurationStore.Hosting/docs/DESIGN.md)
- [Client overview](./Assimalign.Cohesion.ConfigurationStore.Client/docs/OVERVIEW.md)
- [Client design](./Assimalign.Cohesion.ConfigurationStore.Client/docs/DESIGN.md)
