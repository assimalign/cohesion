# ConfigurationStoreResource

Namespace: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

## Purpose

`ConfigurationStoreResource` is the typed `PlannedResource` for an enabled
configuration-store executable. Construction snapshots the supplied
`ResourceManifest`; the snapshot remains the source of truth for artifact, endpoint,
mount, lifecycle, setting, and reference facts.

## Construction

```csharp
var resource = new ConfigurationStoreResource(
    manifest,
    new ConfigurationStoreResourceOptions
    {
        Storage = { Size = "20Gi" },
    });
```

The options argument is optional. A null manifest raises `ArgumentNullException`.

## Planning

`PlannerName` returns `ConfigurationStore planner`. `CreatePlan(context)` requires the
context to name the same resource as the snapshot and emits a platform-neutral
`cohesion/plan/v1` plan. The manifest must describe kind `ConfigurationStore`, a
`StatefulSet`, exactly the `api` endpoint, and exactly the `data` Volume. The resulting
plan has exactly one replica with stable identity, a sized per-replica claim, an API service, and a headless
governing service.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Options](../ConfigurationStoreResourceOptions/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
