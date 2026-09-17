# Assimalign.Cohesion.SecretStore.ApplicationModel — Design

## Design intent

This project is the SecretStore area's AOT-compatible, dependency-guarded
orchestration package. It gives a gateway a typed secret-store resource, a
platform-neutral realization planner, and the default-control-plane factory that
every orchestration-enabled secret-store executable registers. It never references
`SecretStore.Hosting`, a gateway implementation, or a platform object model.

An enabled `Sdk.SecretStore` executable produces `cohesion/resource/v1` at build time.
Generated gateway code passes that `ResourceManifest` to `AddSecretStore(...)`; this
package does not reconstruct artifact, endpoint, mount, or lifecycle facts from
executable naming conventions.

## Family and dependency direction

- `Assimalign.Cohesion.SecretStore` owns the application and secret-store contracts.
- `Assimalign.Cohesion.SecretStore.Hosting` implements the resource runtime, protocol,
  persistence, trust, and certificate workflows.
- `Assimalign.Cohesion.SecretStore.Client` is the thin gateway-side protocol client.
- `Assimalign.Cohesion.SecretStore.ApplicationModel` describes the resource to an
  orchestrator and supplies its default control-plane contract.

The ApplicationModel package does not reference any of the other three. Its only
direct Cohesion references are `Assimalign.Cohesion.ApplicationModel` and
`Assimalign.Cohesion.Hosting.Resources`. The enabled customer executable is the
composition root that receives both the runtime and generated control-plane
registration.

## Manifest-backed resource

`SecretStoreResource` derives from `PlannedResource`. The base snapshots the supplied
`ResourceManifest` and projects its executable, endpoint, and mount compatibility
surfaces. The manifest remains the source of truth for:

- executable or image identity;
- the `api` endpoint;
- the persistent `data` mount and declared size;
- lifecycle limits, application identity, settings, references, and extra
  non-persistent mounts or endpoints; and
- the default control-plane location, `api` plus `/cohesion/v1`.

`SecretStoreResourceOptions` derives from the shared `ResourceOptions` and exposes
only deployer-owned planning overrides. `Storage.Size` overrides the `data` claim's
`mounts[].size` value. The inherited `Replicas` property remains part of the shared
options shape, but the effective replica count must be one. The planner rejects any
manifest or deployer override that requests multiple replicas because the store does
not yet implement replication or consensus. Ports, mount paths, endpoint exposure,
and arbitrary environment values remain executable-owned manifest facts.

## SecretStore planner

`SecretStoreResource.CreatePlan(PlanContext)` is reported as `SecretStore planner` in
application-build diagnostics. Before generic trait mapping, the planner requires:

- manifest kind `SecretStore`, compared ordinally;
- a `StatefulSet` workload;
- an effective replica count of exactly one;
- an `api` endpoint using HTTP or HTTPS over TCP;
- exactly one persistent Volume, named `data`; and
- the default control plane on `api` at `/cohesion/v1`.

Additional endpoints and non-persistent Configuration or Secret mounts remain legal
manifest facts. This permits, for example, a metrics endpoint or a parameter-backed
TLS mount without weakening the single persistent data-owner invariant.

The shared generic planner then produces `cohesion/plan/v1`. The area planner verifies
the resulting stateful shape: exactly one replica with stable workload identity, one
sized per-replica `data` claim, one ordinary API service, and exactly one portless,
headless governing service. A checked-in golden plan pins the default compiler-facing
IR shape.

The result contains only Cohesion realization-plan records. Kubernetes StatefulSets,
claims and Services, Docker volumes, and local directories are produced later by the
selected platform compiler.

## Composition API

`AddSecretStore(manifest, options)` creates a `SecretStoreResource`, applies typed
planning options, and adds it to the application graph. The returned
`ISecretStoreResourceDescriptor` delegates to the descriptor identity owned by the
base graph while exposing `Resource` as the typed `SecretStoreResource`. Planning remains
deferred until `IApplicationBuilder.Build()` so the planner receives the selected
environment and final referenced-manifest map.

```csharp
ISecretStoreResourceDescriptor secrets = builder.AddSecretStore(
    manifest,
    new SecretStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

Generated gateway verbs supply the manifest and expose the same optional typed
options; application authors normally do not load the JSON themselves.

## Default control plane and protocol boundary

`SecretStoreResourceControlPlane.Create()` returns a fresh
`Hosting.Resources.IResourceControlPlane`. The enabled resource's generated
`ResourceControlPlane.g.cs` registers that factory with `ResourceRuntime` and seeds it
with the invocation's observed endpoints. `SecretStore.Hosting` reads the registration
through the shared seam and serves it on the manifest's `api` endpoint; neither package
references the other.

The transport-neutral plane supplies health, readiness, liveness, observed endpoints,
graceful stop, and command discovery. Hosting exposes those operations together with
secret reads, certificate reads, trust-grant enrollment, and the store's protocol
under `/cohesion/v1`. Bootstrap Bearer credentials authenticate managed requests;
credential verification and authorization remain in Hosting.

The plane accepts `cohesion.trust.add` for gateway trust bootstrap and the desired-state
commands `secretstore.add-secret` and `secretstore.issue-certificate`. Only the two desired-state
kinds are advertised in the SDK manifest. Enroll(platformStore) is deferred to item 31t.

## Security ownership

The declarative plane carries no secret bytes, private keys, bootstrap tokens, or
certificate material. Root-CA self-seeding, per-application trusted issuers,
credential verification, intermediate-CA enrollment, leaf issuance, rotation, and
at-rest persistence are runtime behavior in `SecretStore.Hosting`.

The `cohesion.trust.add` identifier is a declarative compatibility fact, not an
authorization decision. Hosting validates the bootstrap credential and enrollment
request before it changes the trusted-issuer set.

## AOT posture

COHAM001 constrains the complete production dependency closure to
`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.ApplicationModel`, plain Hosting,
`Hosting.Health`, `Hosting.Resources`, the permitted ProtectedData facade, and BCL
assemblies. The planner uses typed records and ordinary loops only. Golden
serialization uses `ResourcePlanJsonContext`; there is no reflection, runtime code
generation, SecretStore runtime dependency, or platform SDK dependency.

## Non-goals

- Hosting the API, health endpoints, storage engine, or secret-store protocol.
- Carrying secret values, credentials, private keys, certificates, or trust grants in
  the manifest or realization plan.
- Inventing a manifest from a resource name or executable-name convention.
- Carrying platform-specific scheduling, storage-class, Service, or claim types.
- Client factories or remote protocol calls; those belong to `SecretStore.Client`.
- Horizontal scaling or high availability before a replication and consensus
  protocol is implemented.
- Automatic Platform enrollment before the item 31t certificate contract.

## Declarative commands (item 31c)

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `secretstore.add-secret` | `AddSecret` | secret path |
| `secretstore.issue-certificate` | `IssueCertificate` | certificate name |

Kinds use verb-noun kebab under the area prefix. The examples `rezolvr.record` and
`identityhub.audience` in developer-experience design section 7 are illustrative; item 27's design
rewrite should reflect the landed convention. Manifest commands remain bare JSON strings.
Typed verbs validate argument shape and use source-generated JSON metadata. Build validates the
advertised kind, canonical payload, deterministic id, and uniqueness of the target ownership key.
The default control plane handles id replay and owner isolation; each area handler also accepts
an identical reapplication with a different id. Conflicts return named Rejected details.

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
