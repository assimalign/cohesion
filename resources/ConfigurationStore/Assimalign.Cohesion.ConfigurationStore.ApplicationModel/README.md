# Assimalign.Cohesion.ConfigurationStore.ApplicationModel

The ConfigurationStore area's declarative, AOT-compatible orchestration package. It
wraps an enabled configuration-store executable's build-produced `ResourceManifest`
in a typed resource, produces a platform-neutral `ResourcePlan`, and supplies the
area's default resource-control-plane factory.

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

## Default control plane

`ConfigurationStoreResourceControlPlane.Create()` returns a fresh isolated
`IResourceControlPlane`. An enabled resource's generated
`ResourceControlPlane.g.cs` registers that factory and seeds it with observed
endpoints; `ConfigurationStore.Hosting` consumes the registration through
`Hosting.Resources` and exposes the standard health, readiness, liveness, endpoint,
stop, and command-discovery routes on the manifest's `api` endpoint.

The control plane advertises `configurationstore.add-namespace` alongside
`configurationstore.set-value` and `configurationstore.remove-value`. AddNamespace accepts an
optional seed and creates an owned namespace; the existing typed value verbs operate inside it.

## Dependency boundary

The project has exactly two direct Cohesion dependencies:

- `Assimalign.Cohesion.ApplicationModel`
- `Assimalign.Cohesion.Hosting.Resources`

COHAM001 guards the full production closure. The package never references
ConfigurationStore runtime, Hosting, gateway, or platform libraries, and its plan
contains no Docker or Kubernetes objects.

## Documentation

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
- [Public API](docs/Assembly/Assimalign.Cohesion.ConfigurationStore.ApplicationModel/OVERVIEW.md)
