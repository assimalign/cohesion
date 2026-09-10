# SecretStoreResource

Namespace: `Assimalign.Cohesion.SecretStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel`

## Purpose

`SecretStoreResource` is the typed `PlannedResource` for an enabled secret-store
executable. Construction snapshots the supplied `ResourceManifest`; the snapshot
remains the source of truth for artifact, endpoint, mount, lifecycle, setting,
reference, and control-plane facts.

## Construction

```csharp
var resource = new SecretStoreResource(
    manifest,
    new SecretStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

The options argument is optional. A null manifest raises `ArgumentNullException`.

## Planning

`PlannerName` returns `SecretStore planner`. `CreatePlan(context)` requires the
context to name the same resource as the snapshot and emits a platform-neutral
`cohesion/plan/v1` plan. The manifest must describe kind `SecretStore`, a
`StatefulSet`, an HTTP or HTTPS `api` endpoint, the `api` control plane at
`/cohesion/v1`, exactly one effective replica, and exactly one persistent Volume
named `data`. The resulting plan has stable identity, a sized per-replica claim, an
API service, and a headless governing service. Multiple replicas are rejected until
the store has a replication and consensus protocol.

Additional endpoints and non-persistent mounts retain the generic manifest-to-plan
mapping.

## Exceptions

`CreatePlan` raises `ArgumentNullException` for a null context and
`InvalidOperationException` when the context or SecretStore shape is incompatible.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Options](../SecretStoreResourceOptions/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
