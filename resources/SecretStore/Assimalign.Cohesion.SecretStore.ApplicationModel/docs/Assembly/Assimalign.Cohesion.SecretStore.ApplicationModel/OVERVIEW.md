# Assimalign.Cohesion.SecretStore.ApplicationModel

Namespace: `Assimalign.Cohesion.SecretStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel`

## Purpose

The assembly describes secret-store resources for Cohesion application graphs. It
owns the typed manifest wrapper, platform-neutral planner, composition extension, and
default-control-plane factory; it does not host or implement the secret store.

## Public surface

- [`SecretStoreResource`](SecretStoreResource/OVERVIEW.md) — the manifest-backed
  planned resource.
- [`ISecretStoreResourceDescriptor`](ISecretStoreResourceDescriptor/OVERVIEW.md) —
  the area-typed application-graph descriptor.
- [`SecretStoreResourceOptions`](SecretStoreResourceOptions/OVERVIEW.md) —
  deployer-owned storage override and the shared single-replica option surface.
- [`SecretStoreResourceExtensions`](SecretStoreResourceExtensions/OVERVIEW.md) —
  `AddSecretStore(...)` composition.
- [`SecretStoreResourceControlPlane`](SecretStoreResourceControlPlane/OVERVIEW.md) —
  the area's default-control-plane factory.

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
