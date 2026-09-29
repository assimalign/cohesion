# ConfigurationStoreResourceOptions

Namespace: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

## Purpose

`ConfigurationStoreResourceOptions` supplies deployer-owned planning overrides for a
`ConfigurationStoreResource`. It derives from `ResourceOptions` without adding a
second area-specific options model.

## Surface

- `Replicas` comes from the shared options type, but values other than one are rejected until a replication protocol exists.
- `Storage.Size` overrides the declared size of the per-replica `data` claim when set.

Endpoint names, ports, exposure, mount names and paths, and runtime environment values
remain build-produced manifest facts.

```csharp
var options = new ConfigurationStoreResourceOptions
{
    Storage = { Size = "30Gi" },
};
```

## Links

- [Assembly overview](../OVERVIEW.md)
- [ConfigurationStoreResource](../ConfigurationStoreResource/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
