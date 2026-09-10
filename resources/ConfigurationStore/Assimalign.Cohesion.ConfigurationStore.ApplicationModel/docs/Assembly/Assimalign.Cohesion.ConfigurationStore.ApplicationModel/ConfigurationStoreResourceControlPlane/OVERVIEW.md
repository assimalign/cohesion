# ConfigurationStoreResourceControlPlane

Namespace: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel`

## Purpose

`ConfigurationStoreResourceControlPlane` supplies the default control-plane factory
used by enabled configuration-store executables.

## Create

```csharp
IResourceControlPlane controlPlane =
    ConfigurationStoreResourceControlPlane.Create();
```

Every call returns a fresh, isolated plane with no observed endpoints and the
`configurationstore.set-value` and `configurationstore.remove-value` command kinds.
Generated `ResourceControlPlane.g.cs` registers the factory with
`ResourceRuntime` and observes the invocation's endpoints before
`ConfigurationStore.Hosting` serves the standard control-plane routes on `api`.

Typed `AddNamespace` and `SetValue` descriptor verbs are intentionally deferred to
developer-experience item 31c; this item supplies the underlying wire handlers.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
