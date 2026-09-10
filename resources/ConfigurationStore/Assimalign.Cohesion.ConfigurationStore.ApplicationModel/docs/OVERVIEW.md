# Assimalign.Cohesion.ConfigurationStore.ApplicationModel — Overview

The AOT-compatible, dependency-guarded orchestration package for Cohesion
configuration stores. It turns an enabled configuration-store executable's
build-produced `ResourceManifest` into a typed `ConfigurationStoreResource`, applies
the deployer-owned storage override, and emits a platform-neutral
`ResourcePlan` for the selected gateway compiler.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");

IApplicationResourceDescriptor configuration = builder.AddConfigurationStore(
    manifest,
    new ConfigurationStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

Generated gateway code normally supplies the manifest and exposes these typed
options to application authors.

## Scope

- `ConfigurationStoreResource` — a `PlannedResource` over the immutable manifest snapshot.
- `ConfigurationStoreResourceOptions` — typed `Storage.Size`; replica values other than one are rejected until replication exists.
- `AddConfigurationStore(manifest, options)` — application-graph composition.
- ConfigurationStore planner — exactly one `api` endpoint and one `data` Volume,
  exactly one stable workload replica, a sized per-replica claim, the API service, and a
  headless governing service.
- `ConfigurationStoreResourceControlPlane` — the ConfigurationStore default
  control-plane factory.

The control plane advertises the `configurationstore.set-value` and
`configurationstore.remove-value` wire kinds served by ConfigurationStore.Hosting.
Typed descriptor command verbs remain deferred to developer-experience item 31c.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` for manifests, planned resources, and the
  platform-neutral realization-plan IR.
- `Assimalign.Cohesion.Hosting.Resources` for the default control-plane contract and
  runtime seam.

The project is guarded by COHAM001 and never references ConfigurationStore runtime,
gateway, or platform packages. Runtime endpoint and mount values flow through the
`Hosting.Resources` `ResourceContext` rather than resource-specific environment
variables.

## Links

- [Design](DESIGN.md)
- [Public API](Assembly/Assimalign.Cohesion.ConfigurationStore.ApplicationModel/OVERVIEW.md)
