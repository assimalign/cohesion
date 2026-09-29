# Assimalign.Cohesion.SecretStore

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the
SecretStore area. It also defines the code-first secret-seed and certificate-authority declarations
used by a SecretStore executable's `Program.cs`. The implementation and creation entry point live
in `Assimalign.Cohesion.SecretStore.Hosting`.

## Public surface

- `ISecretStoreApplicationBuilder` owns secret and certificate declarations and builds an `ISecretStoreApplication`.
- `ISecretStoreApplicationBuilder.AddSecret` declares first-start secret bytes without overwriting
  later durable versions.
- `ISecretStoreApplicationBuilder.AddCertificateAuthority` declares the store's root or
  intermediate authority bootstrap.
- `CertificateAuthorityOptions` selects explicit PEM seed material, optional Platform enrollment,
  or standalone self-seeding (the default).
- `ISecretStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`.
- `ISecretStore` is the common disposable lifetime boundary for feature contracts.

The root is declarative and dependency-light. Persistence, trust verification, enrollment,
certificate generation, protocol serving, and cryptographic resource ownership remain runtime
responsibilities; none are performed by this assembly.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `ISecretStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISecretStoreApplicationContext` exposes `ContentRootPath`. `ISecretStoreApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
