# IConfigurationStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.ConfigurationStore`
Assembly: `Assimalign.Cohesion.ConfigurationStore`

## Purpose

`IConfigurationStoreApplicationBuilder` is the public composition seam for a configuration store application. It extends `IHostBuilder` while refining `Build()` to return `IConfigurationStoreApplication`.

## Surface and behavior

- `Build()` creates a configured configuration store application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.ConfigurationStore.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;

IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(args);
await using IConfigurationStoreApplication application = builder.Build();
```
