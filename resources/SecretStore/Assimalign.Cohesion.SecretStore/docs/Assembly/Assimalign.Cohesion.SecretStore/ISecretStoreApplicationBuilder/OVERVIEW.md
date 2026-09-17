# ISecretStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose

`ISecretStoreApplicationBuilder` is the public composition seam for a SecretStore application. It declares area composition and `Build()` returning `ISecretStoreApplication`.

## Surface and behavior

- `AddSecret(string path, ReadOnlyMemory<byte> value)` registers an ordinal path and a snapshot of
  its first-start bytes. It never replaces an existing durable version.
- `AddCertificateAuthority(Action<CertificateAuthorityOptions>? configure = null)` declares one
  authority. Existing durable state wins; first-start selection is explicit PEM material, then
  Platform intermediate enrollment, then the enabled standalone self-seed.
- `Build()` creates a configured SecretStore application.

Secret and certificate byte inputs are snapshotted during registration. Duplicate secret paths and
duplicate certificate-authority declarations are rejected. Hosting service registrations retain insertion
order. The shared host starts the materialized services in that order and stops them in reverse.
The public concrete `SecretStoreApplicationBuilder` lives in `Assimalign.Cohesion.SecretStore.Hosting`.

## Exceptions

`AddSecret` throws `ArgumentException` for a blank path and `InvalidOperationException` for a
duplicate path. `AddCertificateAuthority` rejects invalid or incomplete options and duplicate
declarations. The concrete Hosting builder's `AddService` throws `ArgumentNullException` for a null service or factory. `Build()`
throws `InvalidOperationException` after an earlier build, when a factory returns null, or when
the ambient endpoint/data mount cannot host the store; it otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

SecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
builder.AddSecret("apps/api/client-secret", secretBytes);
builder.AddCertificateAuthority(options =>
{
    options.PlatformEnrollmentEndpoint = platformSecretStore;
    options.PlatformCertificate = platformCertificatePem;
});

await using SecretStoreApplication application = builder.Build();
```

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `ISecretStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ISecretStoreApplicationContext` exposes `ContentRootPath`. `ISecretStoreApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
