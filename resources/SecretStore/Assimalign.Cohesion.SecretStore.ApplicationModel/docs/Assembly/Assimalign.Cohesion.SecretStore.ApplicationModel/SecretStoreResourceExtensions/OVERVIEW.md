# SecretStoreResourceExtensions

Namespace: `Assimalign.Cohesion.SecretStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel`

## Purpose

`SecretStoreResourceExtensions` contributes the C# extension member that adds a
manifest-backed secret store to an `IApplicationBuilder`.

## AddSecretStore

```csharp
ISecretStoreResourceDescriptor secrets = builder.AddSecretStore(
    manifest,
    options);
```

`AddSecretStore(ResourceManifest, SecretStoreResourceOptions?)` creates the typed
resource, adds it to the application graph, and returns the area-typed descriptor used
for dependency chaining. `descriptor.Resource` is a `SecretStoreResource`. Planning
remains deferred until the graph is built, where the planner enforces exactly one
effective replica. A null manifest raises `ArgumentNullException`.

The returned descriptor exposes `AddSecret` and `IssueCertificate` through
SecretStoreResourceCommandExtensions. `Enroll(platformStore)` is deferred to item 31t.

## Links

- [Assembly overview](../OVERVIEW.md)
- [SecretStoreResource](../SecretStoreResource/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
