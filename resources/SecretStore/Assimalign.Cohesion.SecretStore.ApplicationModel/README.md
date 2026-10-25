# Assimalign.Cohesion.SecretStore.ApplicationModel

This package describes resource manifests, dependencies and deployment plans. Its only direct Cohesion dependency is ApplicationModel, whose contracts depend only on Core. Sdk.ApplicationModel generates control-plane registration in the enabled resource executable; Hosting.Resources and the resource host own the runtime surface and apply the deployment record. The gateway observes infrastructure and delivers the record.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");

ISecretStoreResourceDescriptor secrets = builder.AddSecretStore(
    manifest,
    new SecretStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

Generated gateway code normally loads the manifest and calls the same composition
API. The planner preserves the store's stateful contract: a `StatefulSet`, the `api`
endpoint and `/cohesion/v1` control plane, a persistent `data` Volume, exactly one
stable replica, a sized per-replica claim, and a headless governing service. Replica
values other than one are rejected because the store has no replication or consensus
protocol yet.

## Runtime manifest boundary

The producer supplies runtime metadata; this package preserves it without referencing runtime types. The SDK registers the generic control plane in the executable. Producer manifests contain no command catalog or state declarations; configured runtime handlers provide the live state contracts.

The typed descriptor exposes `AddSecret` and `IssueCertificate`. Secret declarations
carry a parameter or resource source reference; literal secret material is forbidden.
`Enroll(platformStore)` is deferred to item 31t's certificate contract.

## Dependency boundary

The project has one direct Cohesion dependency:

- `Assimalign.Cohesion.ApplicationModel`

COHAM001 guards the full production closure. The package never references the
SecretStore runtime, a gateway, or platform libraries, and its plan contains no
Docker or Kubernetes objects.

## Documentation

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
- [API reference (cohesion-docs)](https://github.com/assimalign/cohesion-docs)
