# ISecretStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose

`ISecretStoreApplicationBuilder` is the public composition seam for a SecretStore application. It extends `IHostBuilder` while refining `Build()` to return `ISecretStoreApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new SecretStore context.
- `Build()` creates a configured SecretStore application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.SecretStore.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

ISecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
await using ISecretStoreApplication application = builder.Build();
```
