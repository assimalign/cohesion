# ConfigurationStoreOrchestrationExtensions

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration`

## Purpose

`ConfigurationStoreOrchestrationExtensions` contributes the C# extension members that register a
ConfigurationStore resource as a gateway mount source, on an application builder or on an
application-set member.

## UseConfigurationStore

```csharp
IApplicationResourceDescriptor settings = builder.AddConfigurationStore(Manifests.Settings);
builder.UseConfigurationStore(settings);

set.AddApplication(Applications.AppA, appa => appa.UseConfigurationStore("appa-configuration"));
```

- `UseConfigurationStore(IApplicationResourceDescriptor store)` on `IApplicationBuilder` — the form
  an application built in code uses — returns the builder.
- `UseConfigurationStore(ResourceName store)` on `IApplicationProviderBuilder` — the surface an
  application set hands a member's `AddApplication(declaration, configure)` callback — returns that
  surface.

`IApplicationBuilder` does not extend `IApplicationProviderBuilder`; they are separate interfaces, so
the by-name form is not a builder verb. The default builder implements both and can be cast to
`IApplicationProviderBuilder` to call it.

Both set `Providers.Sources[<store name>]` to a `ConfigurationStoreSourceProvider` and register no
other provider role.

- A repeat call for the same store keeps the existing registration.
- A provider of another type already registered under the store's name raises
  `InvalidOperationException`; the verb never silently replaces a registration it did not make.
- A known resource of another kind raises `ArgumentException` (the by-name form names the
  application), as does a blank name or a reserved source name (`parameter`, `literal`).
- A `null` builder, surface, or store descriptor raises `ArgumentNullException`.

`Build()` copies a builder's registration into the built model's frozen `Providers`; an application
set does the same for a member when its callback returns, attaching it to that member's model alone.
Either way the store is validated as a ConfigurationStore resource of the application, and inside a
set callback a failure is reported as `InvalidOperationException` naming the member.

## Links

- [Assembly overview](../OVERVIEW.md)
- [ConfigurationStoreSourceProvider](../ConfigurationStoreSourceProvider/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
