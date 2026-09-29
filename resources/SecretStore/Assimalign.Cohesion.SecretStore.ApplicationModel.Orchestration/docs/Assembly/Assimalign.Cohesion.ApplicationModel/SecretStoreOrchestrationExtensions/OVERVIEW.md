# SecretStoreOrchestrationExtensions

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

Contributes the `extension(IApplicationBuilder)` and `extension(IApplicationProviderBuilder)`
members that register a SecretStore resource as a gateway provider — on an application builder, or
on an application-set member.

## UseSecretStore

```csharp
SecretStoreProviderBuilder handle = builder.UseSecretStore(secrets);

set.AddApplication(Applications.Platform, platform => platform.UseSecretStore("platform-secrets"));
```

`UseSecretStore(IApplicationResourceDescriptor store)` on an `IApplicationBuilder` (an application
built in code), and `UseSecretStore(ResourceName store)` on an `IApplicationProviderBuilder` (the
member surface an application set hands `AddApplication(declaration, configure)`). The two
interfaces are separate — `IApplicationBuilder` does not extend `IApplicationProviderBuilder` — so
the by-name form is not a builder verb; the default builder implements both and can be cast to call
it. Either form, on its receiver's `ApplicationProviders`:

- sets `Providers.Sources[<store name>]` to a `SecretStoreSourceProvider`;
- adds one `SecretStoreAddSecretInputResolver` to `Providers.CommandInputs` (once per surface,
  whatever the number of stores);
- returns a [`SecretStoreProviderBuilder`](../SecretStoreProviderBuilder/OVERVIEW.md) for the
  optional certificate-authority and trust-store roles.

Calling it again for the same store changes nothing. The by-name form checks the kind of the
resource the surface knows by that name (`TryGetResourceManifest`); for a set member that is the
member's resolved model, so a wrong kind is refused when the set starts, naming the member.
Registrations on a set member apply to that member only.

## Exceptions

- `ArgumentNullException` — the builder / surface, or the `store` descriptor, is `null`.
- `ArgumentException` — `store` is blank, is a known resource with a kind other than `SecretStore`,
  or its name cannot be a mount-source name (for example `parameter`).
- `InvalidOperationException` — another provider type is registered under the store's name, or
  another resolver type is registered for `secretstore.add-secret`. Nothing is written. Inside an
  application-set callback, the set reports any of these as `InvalidOperationException` naming the
  member.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
