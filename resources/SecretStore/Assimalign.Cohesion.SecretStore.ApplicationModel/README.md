# Assimalign.Cohesion.SecretStore.ApplicationModel

The SecretStore area's declarative, AOT-compatible orchestration package. It wraps
an enabled secret-store executable's build-produced `ResourceManifest` in a typed
resource, produces a platform-neutral `ResourcePlan`, and supplies the area's default
resource-control-plane factory.

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

## Default control plane

`SecretStoreResourceControlPlane.Create()` returns a fresh isolated
`IResourceControlPlane` that advertises the generic `cohesion.trust.add` trust-grant
upsert. The enabled resource's generated `ResourceControlPlane.g.cs` registers that
factory and seeds it with observed endpoints. `SecretStore.Hosting` consumes the
registration through `Hosting.Resources` and serves health, readiness, liveness,
observed endpoints, graceful stop, trust bootstrap, secret reads, certificate reads,
and enrollment under `/cohesion/v1` on the manifest's `api` endpoint.

The SecretStore-specific `AddSecret`, `IssueCertificate`, and `Enroll` descriptor
commands belong to developer-experience item 31c and are intentionally not part of
this package yet.

## Dependency boundary

The project has exactly two direct Cohesion dependencies:

- `Assimalign.Cohesion.ApplicationModel`
- `Assimalign.Cohesion.Hosting.Resources`

COHAM001 guards the full production closure. The package never references the
SecretStore runtime, a gateway, or platform libraries, and its plan contains no
Docker or Kubernetes objects.

## Documentation

- [Overview](docs/OVERVIEW.md)
- [Design](docs/DESIGN.md)
- [Public API](docs/Assembly/Assimalign.Cohesion.SecretStore.ApplicationModel/OVERVIEW.md)
