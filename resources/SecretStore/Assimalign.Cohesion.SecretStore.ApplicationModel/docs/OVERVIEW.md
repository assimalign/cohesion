# Assimalign.Cohesion.SecretStore.ApplicationModel — Overview

The AOT-compatible, dependency-guarded orchestration package for Cohesion secret
stores. It turns an enabled secret-store executable's build-produced
`ResourceManifest` into a typed `SecretStoreResource`, applies a deployer-owned
storage override, enforces the single-replica safety boundary, and emits a
platform-neutral `ResourcePlan` for the selected gateway compiler.

```csharp
ResourceManifest manifest = ResourceManifest.Load("resource.json");

ISecretStoreResourceDescriptor secrets = builder.AddSecretStore(
    manifest,
    new SecretStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

Generated gateway code normally supplies the manifest and exposes these typed options
to application authors.

## Scope

- `SecretStoreResource` — a `PlannedResource` over the immutable manifest snapshot.
- `ISecretStoreResourceDescriptor` — the area-typed descriptor returned by the
  composition verb.
- `SecretStoreResourceOptions` — typed `Storage.Size`; the shared `Replicas` value
  must be unset or one until replication exists.
- `AddSecretStore(manifest, options)` — application-graph composition returning the
  typed descriptor whose resource is a `SecretStoreResource`.
- SecretStore planner — `StatefulSet`, an HTTP or HTTPS `api` endpoint, a persistent
  `data` Volume, exactly one stable replica, a sized per-replica claim, endpoint
  services, and a headless governing service.
- `SecretStoreResourceControlPlane` — the SecretStore default control-plane factory,
  including the generic `cohesion.trust.add` trust-grant operation.

`SecretStore.Hosting` exposes the control plane and store protocol beneath
`/cohesion/v1` on `api`. The transport-neutral plane aggregates health, readiness and
liveness, publishes observed endpoints, and supports graceful stop. Secret and
certificate retrieval, trust enrollment, credential verification, and persistent
store behavior remain runtime responsibilities.

The future SecretStore domain command verbs `AddSecret`, `IssueCertificate`, and
`Enroll` are deliberately deferred to developer-experience item 31c.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` for manifests, planned resources, and the
  platform-neutral realization-plan IR.
- `Assimalign.Cohesion.Hosting.Resources` for the default-control-plane contract and
  runtime registration seam.

The project is guarded by COHAM001 and never references SecretStore runtime, gateway,
or platform packages. Runtime endpoint and mount values flow through the shared
`Hosting.Resources` context rather than SecretStore-specific environment variables.

## Links

- [Design](DESIGN.md)
- [Public API](Assembly/Assimalign.Cohesion.SecretStore.ApplicationModel/OVERVIEW.md)
