# ISecretStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose

`ISecretStoreApplicationBuilder` is the public composition seam for a SecretStore application. It extends `IHostBuilder` while refining `Build()` to return `ISecretStoreApplication`.

## Surface and behavior

- `AddSecret(string path, ReadOnlyMemory<byte> value)` registers an ordinal path and a snapshot of
  its first-start bytes. It never replaces an existing durable version.
- `AddCertificateAuthority(Action<CertificateAuthorityOptions>? configure = null)` declares one
  authority. Existing durable state wins; first-start selection is explicit PEM material, then
  Platform intermediate enrollment, then the enabled standalone self-seed.
- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new SecretStore context.
- `Build()` creates a configured SecretStore application.

Secret and certificate byte inputs are snapshotted during registration. Duplicate secret paths and
duplicate certificate-authority declarations are rejected. Service registrations retain insertion
order. The shared host starts the materialized services in that order and stops them in reverse.
The concrete builder remains internal to `Assimalign.Cohesion.SecretStore.Hosting`.

## Exceptions

`AddSecret` throws `ArgumentException` for a blank path and `InvalidOperationException` for a
duplicate path. `AddCertificateAuthority` rejects invalid or incomplete options and duplicate
declarations. `AddService` throws `ArgumentNullException` for a null service or factory. `Build()`
throws `InvalidOperationException` after an earlier build, when a factory returns null, or when
the ambient endpoint/data mount cannot host the store; it otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

ISecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
builder.AddSecret("apps/api/client-secret", secretBytes);
builder.AddCertificateAuthority(options =>
{
    options.PlatformEnrollmentEndpoint = platformSecretStore;
    options.PlatformCertificate = platformCertificatePem;
});

await using ISecretStoreApplication application = builder.Build();
```
