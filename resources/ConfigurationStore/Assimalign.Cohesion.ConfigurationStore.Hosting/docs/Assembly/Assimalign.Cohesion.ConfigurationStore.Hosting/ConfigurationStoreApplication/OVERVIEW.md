# ConfigurationStoreApplication

Namespace: `Assimalign.Cohesion.ConfigurationStore.Hosting`
Assembly: `Assimalign.Cohesion.ConfigurationStore.Hosting`

## Purpose

`ConfigurationStoreApplication` is the public factory for the configuration store hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IConfigurationStoreApplicationBuilder`.
- When generated resource registration is present, the builder consumes the ambient endpoint,
  volume, environment, trust key, and credential through `ResourceRuntime`.
- A plain application accepts `--endpoint <http-uri>` and `--data <directory>`.
- Building materializes explicit host services, durable declarations, and the protocol listener.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;

IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(args);
builder.AddNamespace("app", ns => ns.Set("Mode", "production"));
await using IConfigurationStoreApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
