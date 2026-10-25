# Assimalign.Cohesion.ConfigurationStore.ApplicationModel

This package describes resource manifests, dependencies and deployment plans. Its only direct Cohesion dependency is ApplicationModel, whose contracts depend only on Core. Sdk.ApplicationModel generates control-plane registration in the enabled resource executable; Hosting.Resources and the resource host own the runtime surface and apply the deployment record. The gateway observes infrastructure and delivers the record.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");

IApplicationResourceDescriptor configuration = builder.AddConfigurationStore(
    manifest,
    new ConfigurationStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

Generated gateway code normally loads the manifest and calls the same composition
API. The planner preserves the configuration store's stateful contract: one `api`
endpoint, one `data` volume, exactly one stable replica, a sized per-replica claim,
and a headless governing service.

The ConfigurationStore SDK defaults `CohesionMaxReplicas` to `1`; its local durable store has no
replication protocol, so scaling must not happen accidentally.

## Runtime manifest boundary

The producer supplies runtime metadata; this package preserves it without referencing runtime types. The SDK registers the generic control plane in the executable. Producer manifests contain no command catalog or state declarations; configured runtime handlers provide the live state contracts.

The control plane advertises `configurationstore.add-namespace` alongside
`configurationstore.set-value` and `configurationstore.remove-value`. AddNamespace accepts an
optional seed and creates an owned namespace; the existing typed value verbs operate inside it.

## Dependency boundary

The project has one direct Cohesion dependency:

- `Assimalign.Cohesion.ApplicationModel`

COHAM001 guards the full production closure. The package never references
ConfigurationStore runtime, Hosting, gateway, or platform libraries, and its plan
contains no Docker or Kubernetes objects.

## Documentation

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
- [API reference (cohesion-docs)](https://github.com/assimalign/cohesion-docs)
