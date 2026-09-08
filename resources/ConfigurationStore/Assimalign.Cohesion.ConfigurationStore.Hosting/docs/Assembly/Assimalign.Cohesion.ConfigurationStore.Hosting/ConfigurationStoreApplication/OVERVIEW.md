# ConfigurationStoreApplication

Namespace: `Assimalign.Cohesion.ConfigurationStore.Hosting`
Assembly: `Assimalign.Cohesion.ConfigurationStore.Hosting`

## Purpose

`ConfigurationStoreApplication` is the public factory for the configuration store hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IConfigurationStoreApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its explicitly registered host services in registration order; no services are registered automatically.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;

IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(args);
await using IConfigurationStoreApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
