# IConfigurationStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.ConfigurationStore`
Assembly: `Assimalign.Cohesion.ConfigurationStore`

## Purpose

`IConfigurationStoreApplicationBuilder` is the public composition seam for a configuration store application. It extends `IHostBuilder` while refining `Build()` to return `IConfigurationStoreApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured configuration store application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.ConfigurationStore.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;

IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(args);
await using IConfigurationStoreApplication application = builder.Build();
```
