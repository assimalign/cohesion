# Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration

## Summary

The opt-in package that lets a gateway use a SecretStore resource of its application. It
implements the gateway-side provider seams of `Assimalign.Cohesion.ApplicationModel` over the
SecretStore control plane, through the Core-only `Assimalign.Cohesion.SecretStore.Client`:

| Role | Type | Seam |
| --- | --- | --- |
| `<store>:<key>` Secret mounts — secret values and endpoint certificates | `SecretStoreSourceProvider` | `IResourceSourceProvider` |
| The application's certificate authority | `SecretStoreCertificateAuthority` | `IResourceCertificateAuthority` |
| The application's trusted-issuer store | `SecretStoreTrustedIssuerStore` | `ITrustedIssuerStore` |
| `secretstore.add-secret` payload resolution at delivery time | `SecretStoreAddSecretInputResolver` | `IResourceCommandInputResolver` |

Nothing is registered by convention. A gateway that wants any of this references the package and
calls `builder.UseSecretStore(store)` in its `Program.cs` — or, in an application-set gateway,
`UseSecretStore("<store>")` in the callback of the member whose store it is.

## Usage

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationResourceDescriptor secrets = builder.AddSecretStore(Manifests.Secrets);

builder.UseSecretStore(secrets)       // Sources["secrets"] + the add-secret resolver
    .AsCertificateAuthority()         // Providers.CertificateAuthority = (secrets, authority)
    .AsTrustStore();                  // Providers.TrustStore = (secrets, trust store)
```

- `UseSecretStore` registers the store as the source of every `secrets:<key>` Secret mount and
  adds one `SecretStoreAddSecretInputResolver` to `Providers.CommandInputs`. An application that
  declares `AddSecret` needs that resolver: the SecretStore manifest marks `secretstore.add-secret`
  `requiresInputResolver`, so `Build()` fails without it, naming this package and
  `builder.UseSecretStore(...)`. The store's rejection of an unresolved add-secret remains as
  defense in depth.
- `AsCertificateAuthority` makes the store issue the TLS leaf of every endpoint that declares a
  certificate mount without a source (`certs/<resource>-<endpoint>`).
- `AsTrustStore` persists `cohesion trust add` grants in the store (`cohesion.trust.add`) and reads
  them back from its `trusted-issuers.json` export.

The calls are idempotent for the same store. They never silently replace a registration they did
not make: a different provider under the store's name, a different `secretstore.add-secret`
resolver, or another certificate authority or trust store is an `InvalidOperationException`.

### In an application-set gateway

A set member's model comes from its own gateway's describe output, which carries no providers, so
the set gateway registers the member's store where it adds the member, by name, on the
`IApplicationProviderBuilder` the set hands the callback. The by-name overload is the member form;
`IApplicationBuilder` is a separate interface, so an application built in code uses the descriptor
overload above. Both write the same `ApplicationProviders`.

```csharp
using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;

IApplicationSet set = Application.CreateSet(new LocalGateway(options), args)
    .AddApplication(Applications.Platform, platform => platform
        .UseSecretStore("platform-secrets")   // platform's own SecretStore resource
        .AsCertificateAuthority()
        .AsTrustStore())
    .AddApplication(Applications.AppA, appa => appa.UseSecretStore("appa-secrets"))
    .AddApplication(Applications.Networking);

await set.RunAsync();
```

Each member gets only what its own callback registers: `appa` does not inherit `platform`'s
certificate authority or trust store, and two members with a store of the same name each bind their
own. When the member declares the named resource with another kind, the call is refused at set start
naming the member; a store-backed member without `UseSecretStore` — one whose resources read the
store, or that declares `AddSecret` for it — fails at set start with the member name and the
`application.UseSecretStore("<store>")` call to add.

## Dependencies

- `Assimalign.Cohesion.ApplicationModel` — the provider seams (`ApplicationProviders` and the
  interfaces above).
- `Assimalign.Cohesion.SecretStore.Client` — the Core-only HTTP protocol client.

The package references no `Assimalign.Cohesion.Hosting*` library, no
`Assimalign.Cohesion.ApplicationModel.Gateway*` library, and not
`Assimalign.Cohesion.SecretStore.ApplicationModel` (`COHRES001`/`COHRES003`/`COHRES004`). It is a
NuGet-only package and never a member of the `App.SecretStore` shared framework. Its types share
the `Assimalign.Cohesion.ApplicationModel` namespace, so one `using` composes a gateway; they are
area-prefixed to stay unambiguous there.

## Status

`Assimalign.Cohesion.ApplicationModel.Gateway` has no built-in store client any more: it resolves
SecretStore sources, default certificates, trusted issuers, and `secretstore.add-secret` payloads
only through the providers this package registers, and `Build()` validates those registrations
against the model, including the add-secret resolver wherever `AddSecret` is declared. This package
reproduces the gateway's former behaviour request for request;
[DESIGN.md](DESIGN.md) records the equivalence and the few deliberate differences. A Local
application gets that behaviour back by registering every role:
`builder.UseSecretStore(store).AsCertificateAuthority().AsTrustStore()`.

## Documentation

- [Design](DESIGN.md)
- [API reference](Assembly/Assimalign.Cohesion.ApplicationModel/OVERVIEW.md)
- [SecretStore area](../../README.md)
- [SecretStore client](../../Assimalign.Cohesion.SecretStore.Client/docs/OVERVIEW.md)
