# SecretStoreApplication

Namespace: `Assimalign.Cohesion.SecretStore.Hosting`
Assembly: `Assimalign.Cohesion.SecretStore.Hosting`

## Purpose

`SecretStoreApplication` is the public factory for the SecretStore runtime. The runtime builder,
host, context, persistence, certificate-authority, and endpoint types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `ISecretStoreApplicationBuilder`.
- The builder snapshots the current `ResourceRuntime.Current` context. In-process callers and tests
  install their invocation with `ResourceRuntime.CreateScope(...)` before calling the factory.
- `--endpoint <uri>`/`--endpoint=<uri>` and `--data <path>`/`--data=<path>` provide standalone
  fallbacks after ambient endpoint and mount values.
- Building materializes registered service factories once in order and appends the protected
  SecretStore HTTP endpoint. The endpoint starts last and drains first.
- A builder may be built only once.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

ISecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
builder.AddSecret("app/api-key", secretBytes);
builder.AddCertificateAuthority();
await using ISecretStoreApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

For an enabled resource, generated code registers the area control plane through
`Hosting.Resources`; Hosting consumes that registration without referencing
`SecretStore.ApplicationModel`.
