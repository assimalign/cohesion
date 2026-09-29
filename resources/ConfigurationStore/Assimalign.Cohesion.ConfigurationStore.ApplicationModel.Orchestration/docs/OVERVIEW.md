# Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration

## Summary

This opt-in package lets a gateway resolve `Configuration` mounts from a ConfigurationStore
resource. A mount whose source is `<store>:<namespace>` receives the named namespace of that store.
Nothing registers it by convention: the gateway project references the package and calls
`builder.UseConfigurationStore(store)` in its `Program.cs`.

The package references only `Assimalign.Cohesion.ApplicationModel` (the provider seams) and
`Assimalign.Cohesion.ConfigurationStore.Client` (the Core-only wire client). It ships as a NuGet
package only and is never a member of the `App.ConfigurationStore` shared framework.

## Public surface

- `ConfigurationStoreSourceProvider` implements `IResourceSourceProvider` with `ResourceKind`
  `ConfigurationStore`. `ReadConfigurationAsync` reads one namespace. `ReadSecretAsync` and
  `ReadCertificateAsync` keep the interface defaults, which throw `NotSupportedException`.
- `ConfigurationStoreOrchestrationExtensions` declares two registration verbs:
  - `extension(IApplicationBuilder)` `UseConfigurationStore(IApplicationResourceDescriptor store)`,
    which returns the builder;
  - `extension(IApplicationProviderBuilder)` `UseConfigurationStore(ResourceName store)`, the by-name
    form an application-set member uses (its model is imported, so it has no descriptors), which
    returns the surface it was called on.

  Both set `Providers.Sources[<store name>]` to a `ConfigurationStoreSourceProvider`. A repeat call
  keeps that registration; a provider of another type already registered under the store's name is
  an `InvalidOperationException`, never silently replaced. A known resource of another kind is an
  `ArgumentException`.

Both types live in the shared `Assimalign.Cohesion.ApplicationModel` namespace, so the gateway's
single `using` composes them next to the area verbs.

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
IApplicationResourceDescriptor settings = builder.AddConfigurationStore(Manifests.Settings);
builder.AddResource(Manifests.Api);      // its manifest mounts "settings:api" as Configuration
builder.UseConfigurationStore(settings); // "settings:<namespace>" now resolves through the store
```

### In an application-set gateway

A set member's model comes from its own gateway's describe output, which carries no providers, so
the set gateway registers the member's store by name where it adds the member:

```csharp
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

IApplicationSet set = Application.CreateSet(new LocalGateway(options), args)
    .AddApplication(Applications.AppA, appa => appa.UseConfigurationStore("appa-configuration"))
    .AddApplication(Applications.AppB, appb => appb.UseConfigurationStore("appb-configuration"))
    .AddApplication(Applications.Networking);

await set.RunAsync();
```

Each member gets only its own registration, even when two members name their stores alike. A
member whose manifests read a store it did not register fails at set start, naming the member and
the `application.UseConfigurationStore("<store>")` call to add.

## Behavior

For each `settings:<namespace>` Configuration mount, the gateway passes the provider a
`ResourceSourceRequest`. The request's `Store` is a `ResourceProviderConnection` to the store,
with the store's control-plane address, a bearer credential whose audience is the store, and the
TLS validator for the store's certificate. The provider sends
`GET /cohesion/v1/namespaces?name=<namespace>` to the root of that endpoint through
`ConfigurationStoreClient`, and returns the namespace entries with any `null` values kept. The
gateway writes them to the mount as a JSON object with ordinally sorted keys.

HTTP failures, including `404` for a namespace the store does not hold, surface as
`HttpRequestException` with the store's status code. A malformed document surfaces as
`JsonException`, a request for a Secret mount as `NotSupportedException`, and a request without a
ConfigurationStore connection as `ArgumentException`. The gateway reports each of these as an
unresolved mount input, as it did before the provider seams.

## Links

- [Design](./DESIGN.md)
- [API reference](./Assembly/Assimalign.Cohesion.ApplicationModel/OVERVIEW.md)
- [Area README](../../README.md)
- [ConfigurationStore.Client design](../../Assimalign.Cohesion.ConfigurationStore.Client/docs/DESIGN.md)
