# Assimalign.Cohesion.ConfigurationStore.ApplicationModel

Namespace: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

## Purpose

The assembly describes configuration-store resources for Cohesion application graphs.
It owns the typed manifest wrapper, platform-neutral planner, composition extension,
and default control-plane factory; it does not host or implement the configuration
service.

## Public surface

- [`ConfigurationStoreResource`](ConfigurationStoreResource/OVERVIEW.md) — the
  manifest-backed planned resource.
- [`ConfigurationStoreResourceOptions`](ConfigurationStoreResourceOptions/OVERVIEW.md)
  — deployer-owned storage override and the singleton constraint.
- [`ConfigurationStoreResourceExtensions`](ConfigurationStoreResourceExtensions/OVERVIEW.md)
  — typed `AddConfigurationStore(...)` and `RemoteReferenceConfigurationStore(...)` composition.
- [`ConfigurationStoreResourceControlPlane`](ConfigurationStoreResourceControlPlane/OVERVIEW.md)
  — the area's default control-plane factory.

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)

`IConfigurationStoreResourceDescriptor` retains typed dependency chaining and declarative commands.
`ConfigurationStoreResourceCommandExtensions` contributes `SetValue` and `RemoveValue`; both use the shared `IResourceCommandDescriptor` seam and source-generated payload metadata.
