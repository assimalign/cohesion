# ConfigurationStoreResourceExtensions

Namespace: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

## Purpose

`ConfigurationStoreResourceExtensions` contributes the C# extension member that adds
a manifest-backed configuration store to an `IApplicationBuilder`.

## AddConfigurationStore

```csharp
IConfigurationStoreResourceDescriptor configuration = builder.AddConfigurationStore(
    manifest,
    options);
```

`AddConfigurationStore(ResourceManifest, ConfigurationStoreResourceOptions?)` creates
the typed resource, adds it to the application graph, and returns the ordinary
`IConfigurationStoreResourceDescriptor` used for dependency chaining. Planning remains
deferred until the graph is built. A null manifest raises `ArgumentNullException`.

## Links

- [Assembly overview](../OVERVIEW.md)
- [ConfigurationStoreResource](../ConfigurationStoreResource/OVERVIEW.md)
- [Project design](../../../DESIGN.md)

`RemoteReferenceConfigurationStore(declaration, configure)` returns the same typed descriptor for a manifest-backed external. Typed descriptors support `SetValue` and `RemoveValue` command declarations.
