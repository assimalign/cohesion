# SecretStoreApplication

Namespace: `Assimalign.Cohesion.SecretStore.Hosting`
Assembly: `Assimalign.Cohesion.SecretStore.Hosting`

## Purpose

`SecretStoreApplication` is the public concrete application and creation entry point for the SecretStore runtime. The application, builder, and context are public. Persistence, certificate-authority, and endpoint types remain internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns a `SecretStoreApplicationBuilder`.
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

SecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
builder.AddSecret("app/api-key", secretBytes);
builder.AddCertificateAuthority();
await using SecretStoreApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

For an enabled resource, generated code registers the area control plane through
`Hosting.Resources`; Hosting consumes that registration without referencing
`SecretStore.ApplicationModel`.

## Concrete composition (T10 / O34)

`SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `SecretStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SecretStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
