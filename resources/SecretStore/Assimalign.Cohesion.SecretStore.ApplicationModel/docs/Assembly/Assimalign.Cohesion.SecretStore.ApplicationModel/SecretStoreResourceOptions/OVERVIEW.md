# SecretStoreResourceOptions

Namespace: `Assimalign.Cohesion.SecretStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel`

## Purpose

`SecretStoreResourceOptions` supplies deployer-owned planning overrides for a
`SecretStoreResource`. It derives from `ResourceOptions` without introducing a
second area-specific storage model.

## Surface

- `Replicas` comes from the shared options type, but must be unset or one until a
  replication protocol exists.
- `Storage.Size` overrides the declared size of the per-replica `data` claim when set.

Endpoint names, ports, exposure, mount names and paths, and runtime environment values
remain build-produced manifest facts.

```csharp
var options = new SecretStoreResourceOptions
{
    Storage = { Size = "30Gi" },
};
```

The planner rejects an effective replica count other than one, whether it comes from
the manifest or a deployer override. This prevents unsafe independent secret-store
instances while replication and consensus are unavailable. The planner also requires
the final `data` size to be non-empty.

## Links

- [Assembly overview](../OVERVIEW.md)
- [SecretStoreResource](../SecretStoreResource/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
