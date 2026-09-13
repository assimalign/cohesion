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

The `AddSecret` and `IssueCertificate` descriptor verbs declare source references and certificate
identities. `Enroll(platformStore)` is deferred to item 31t.

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

## Commands

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `secretstore.add-secret` | `AddSecret` | secret path |
| `secretstore.issue-certificate` | `IssueCertificate` | certificate name |

AddSecret declarations carry only a source reference. `parameter:<name>` resolves through the
gateway's existing parameter provider before delivery. `<resource>:<key>` uses the existing store
resolver and requires a declared dependency and an available source endpoint. `literal:<value>`
is rejected during declaration construction: literal secret material never enters the desired
model, deterministic id or manifest. Only the transient delivery envelope contains resolved bytes;
the protected repository stores the value and source together. An unresolved source is a named
Rejected result. Original source-only commands remain the gateway's declaration ledger.

IssueCertificate honors the supplied subject and SAN set. An existing certificate with different
identity is rejected until deleted; renewal preserves its identity. Private key and leaf storage
reuse the existing protected CA repository.

The control plane also accepts `cohesion.trust.add`, which the SDK manifest deliberately does not
advertise. It is the gateway-owned trust channel through IGatewayStoreClient, never a Build-declared
application command. Trust keeps owner `issuer@subject`, POST-only behavior, empty 204 success,
empty 409 conflict and existing 403 authorization refusals. New commands use owner `issuer`, accept
POST and DELETE, return 200 application/octet-stream on success, and JSON `{status,detail}` refusals.
The client accepts empty successful responses as Applied (Deleted for DELETE); legacy SendCommandAsync
continues to work. This owner split lets local gateway declarations authenticate end to end.

Restricted trust grants accept `{trustKey,allowedCommandKinds}` while unrestricted grants retain
the bare JWK payload. The protected trust document round-trips the optional string array; absent
or empty means every command kind is allowed. Enroll(platformStore) is deferred to item 31t:
automatic Platform enrollment needs a gateway-owned mediator and Platform-audience signer.
