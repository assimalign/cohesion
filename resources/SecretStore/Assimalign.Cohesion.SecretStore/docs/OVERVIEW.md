# Assimalign.Cohesion.SecretStore

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the
SecretStore area. It also defines the code-first secret-seed and certificate-authority declarations
used by a SecretStore executable's `Program.cs`. The implementation and creation entry point live
in `Assimalign.Cohesion.SecretStore.Hosting`.

## Public surface

- `ISecretStoreApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `ISecretStoreApplication`.
- `ISecretStoreApplicationBuilder.AddSecret` declares first-start secret bytes without overwriting
  later durable versions.
- `ISecretStoreApplicationBuilder.AddCertificateAuthority` declares the store's root or
  intermediate authority bootstrap.
- `CertificateAuthorityOptions` selects explicit PEM seed material, optional Platform enrollment,
  or standalone self-seeding (the default).
- `ISecretStoreApplication` exposes the shared host lifecycle plus `RunAsync`.
- `ISecretStore` is the common disposable lifetime boundary for feature contracts.

The root is declarative and dependency-light. Persistence, trust verification, enrollment,
certificate generation, protocol serving, and cryptographic resource ownership remain runtime
responsibilities; none are performed by this assembly.
