# Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

The assembly implements the gateway-side provider seams of `Assimalign.Cohesion.ApplicationModel`
over a SecretStore resource's control plane. A gateway opts in by referencing the package and
calling `builder.UseSecretStore(store)`, or, for an application-set member,
`member.UseSecretStore("<store>")` in that member's `AddApplication` callback.

## Public surface

- [`SecretStoreOrchestrationExtensions`](SecretStoreOrchestrationExtensions/OVERVIEW.md) —
  `builder.UseSecretStore(store)` and `member.UseSecretStore("<store>")`.
- [`SecretStoreProviderBuilder`](SecretStoreProviderBuilder/OVERVIEW.md) — the handle that assigns
  the certificate-authority and trust-store roles.
- [`SecretStoreSourceProvider`](SecretStoreSourceProvider/OVERVIEW.md) — `<store>:<key>` secret and
  certificate mounts (`IResourceSourceProvider`).
- [`SecretStoreCertificateAuthority`](SecretStoreCertificateAuthority/OVERVIEW.md) — store-issued
  endpoint leaves (`IResourceCertificateAuthority`).
- [`SecretStoreTrustedIssuerStore`](SecretStoreTrustedIssuerStore/OVERVIEW.md) — persisted peer
  trust grants (`ITrustedIssuerStore`).
- [`SecretStoreAddSecretInputResolver`](SecretStoreAddSecretInputResolver/OVERVIEW.md) —
  `secretstore.add-secret` payload resolution (`IResourceCommandInputResolver`).

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
