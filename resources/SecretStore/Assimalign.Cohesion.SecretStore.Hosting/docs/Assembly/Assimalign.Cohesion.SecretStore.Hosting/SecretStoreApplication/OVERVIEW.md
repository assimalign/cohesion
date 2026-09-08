# SecretStoreApplication

Namespace: `Assimalign.Cohesion.SecretStore.Hosting`
Assembly: `Assimalign.Cohesion.SecretStore.Hosting`

## Purpose

`SecretStoreApplication` is the public factory for the SecretStore hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `ISecretStoreApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its registered service factories once against the new SecretStore context and preserves registration order; no services are added by default.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

ISecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
await using ISecretStoreApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
