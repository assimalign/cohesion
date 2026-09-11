# Assimalign.Cohesion.ConfigurationStore.ApplicationModel — Design

## Intent

This project is the ConfigurationStore area's AOT-compatible, dependency-guarded
orchestration package. It gives a gateway a typed configuration-store resource, a
platform-neutral realization planner, and the default control-plane factory that
every orchestration-enabled configuration-store executable registers. It never
references `ConfigurationStore.Hosting`, a gateway implementation, or a platform
object model.

An enabled `Sdk.ConfigurationStore` executable produces `cohesion/resource/v1` at
build time. Generated gateway code passes that `ResourceManifest` to
`AddConfigurationStore(...)`; this package does not reconstruct endpoint, mount,
artifact, or lifecycle facts from conventions.

## Manifest-backed resource

`ConfigurationStoreResource` derives from `PlannedResource`. The base snapshots the
supplied `ResourceManifest` and projects its executable, endpoint, and mount
compatibility surfaces. The manifest remains the source of truth for:

- executable or image identity;
- the `api` endpoint and its probes;
- the persistent `data` mount and declared size;
- lifecycle limits, application identity, settings, and references; and
- the default control-plane location.

`ConfigurationStoreResourceOptions` derives from the shared `ResourceOptions` and
exposes deployer-owned planning overrides. `Storage.Size` overrides the `data` claim's
`mounts[].size` value. The shared `Replicas` property is present, but this planner
rejects values other than one until a replication protocol exists. Ports, mount paths,
endpoint exposure, and arbitrary environment values remain executable-owned manifest
facts.

## ConfigurationStore planner

`ConfigurationStoreResource.CreatePlan(PlanContext)` is reported as
`ConfigurationStore planner` in application-build diagnostics. Before generic trait
mapping, the planner requires:

- manifest kind `ConfigurationStore`, compared ordinally;
- a `StatefulSet` workload;
- exactly one endpoint, named `api`; and
- exactly one mount, the `data` `Volume`.

The shared generic planner then produces `cohesion/plan/v1`. The area planner verifies
the resulting stateful shape: exactly one replica with stable workload identity, exactly one sized
per-replica `data` claim, one ordinary API service, and exactly one portless,
headless governing service. A checked-in golden plan pins that complete compiler-facing
IR shape.

The result contains only Cohesion realization-plan records. Kubernetes StatefulSets,
claims and services, Docker volumes, and local directories are produced later by the
selected platform compiler.

## Composition API

`AddConfigurationStore(manifest, options)` creates a
`ConfigurationStoreResource`, applies the typed storage override, and adds
it to the application graph. Planning remains deferred until
`IApplicationBuilder.Build()` so the planner receives the selected environment and
the final referenced-manifest map.

```csharp
IConfigurationStoreResourceDescriptor configuration = builder.AddConfigurationStore(
    manifest,
    new ConfigurationStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

Generated gateway verbs supply the manifest and expose the same optional typed
options; application authors normally do not load the JSON themselves.

## Default control plane

`ConfigurationStoreResourceControlPlane.Create()` returns a fresh
`Hosting.Resources.IResourceControlPlane`. The enabled resource's generated
`ResourceControlPlane.g.cs` registers that factory with `ResourceRuntime` and seeds it
with the invocation's observed endpoints. `ConfigurationStore.Hosting` reads the
registration through the shared seam and serves it on the manifest's `api` endpoint;
neither package references the other.

The hosting terminal provides `GET`/`HEAD` health, readiness, liveness, endpoint, and
command-discovery routes under `/cohesion/v1`, plus `POST /cohesion/v1/stop` and
`POST /cohesion/v1/commands`. The short `/healthz`, `/readyz`, and `/livez` probe paths
are also available. Namespaced routes use the ambient bootstrap Bearer credential
when one is present and fail closed for managed contexts without a credential.

The accepted command-kind set contains `configurationstore.set-value` and
`configurationstore.remove-value`, whose wire handlers live in Hosting. Typed `SetValue` and `RemoveValue` descriptor verbs now declare those two wire kinds.
`AddNamespace` remains deferred until a distinct namespace-ownership handler and command kind land.

## Dependency and AOT posture

COHAM001 constrains the complete production dependency closure to
`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.ApplicationModel`,
`Assimalign.Cohesion.Hosting`, `Assimalign.Cohesion.Hosting.Health`,
`Assimalign.Cohesion.Hosting.Resources`, and the permitted BCL surface. The planner
uses typed records and ordinary loops only. Golden serialization uses
`ResourcePlanJsonContext`; there is no reflection, runtime code generation,
ConfigurationStore runtime dependency, or platform SDK dependency.

## Non-goals

- Hosting the API, health endpoints, storage engine, or configuration protocol.
- Inventing a manifest from a resource name or executable-name convention.
- Carrying platform-specific scheduling, storage-class, service, or claim types.
- Client factories or remote protocol calls; those belong to
  `ConfigurationStore.Client`.
- AddNamespace and a separate namespace-ownership wire command; the two landed value kinds are the current scope.

## Typed descriptor commands

`AddConfigurationStore` returns `IConfigurationStoreResourceDescriptor`.
`RemoteReferenceConfigurationStore(declaration, configure)` binds a manifest-backed external
with the same surface. The internal wrapper retains the registered graph resource identity.

`SetValue(namespaceName, key, JsonElement|string value, optional: false)` records
`configurationstore.set-value` with `{ "namespace", "key", "value" }`; values must be strings or null, matching the landed store contract.
`RemoveValue(namespaceName, key, optional: false)` records `configurationstore.remove-value`
with `{ "namespace", "key" }`. Both use `namespace/key` as the ownership conflict key and
source-generated payload metadata; JSON properties are canonicalized by ApplicationModel.
Value keys cannot contain `/`; namespace names may, so different values cannot alias the same
ownership key. A model may declare one set or remove command per target/key.
These declarations carry ordinary configuration, never secrets, because model export retains them.
`Build()` checks the manifest's accepted kinds; delivery and mutation remain gateway/Hosting work.
