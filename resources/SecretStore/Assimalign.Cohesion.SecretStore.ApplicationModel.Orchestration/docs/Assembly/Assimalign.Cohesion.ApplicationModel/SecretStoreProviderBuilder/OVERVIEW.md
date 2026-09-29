# SecretStoreProviderBuilder

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

The handle `UseSecretStore` returns. It assigns the optional single-slot gateway roles of the store
it was created for. It has no public constructor.

```csharp
builder.UseSecretStore(secrets)
    .AsCertificateAuthority()
    .AsTrustStore();

set.AddApplication(Applications.Platform, platform => platform
    .UseSecretStore("platform-secrets")
    .AsCertificateAuthority()
    .AsTrustStore());
```

## Properties

- `Providers` — the `ApplicationProviders` the store was registered in:
  `IApplicationBuilder.Providers` for an application built in code, or
  `IApplicationProviderBuilder.Providers` for an application-set member. The roles are written to
  this instance, so they belong to that application alone.
- `Store` — the store resource's `ResourceName`.

## Methods

- `AsCertificateAuthority()` — sets `Providers.CertificateAuthority` to
  `(Store, SecretStoreCertificateAuthority)`. Returns the handle.
- `AsTrustStore()` — sets `Providers.TrustStore` to `(Store, SecretStoreTrustedIssuerStore)`.
  Returns the handle.

Both are idempotent for the same store. Either throws `InvalidOperationException`, and changes
nothing, when its slot is already bound to another resource or another provider type.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
