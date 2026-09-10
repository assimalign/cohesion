# IConfigurationStoreApplication

Namespace: `Assimalign.Cohesion.ConfigurationStore`
Assembly: `Assimalign.Cohesion.ConfigurationStore`

## Purpose

`IConfigurationStoreApplication` is the public lifecycle contract for a configuration store application. It extends `IHost` and adds the run-until-shutdown operation used by executable resources.

## Surface

- `RunAsync(CancellationToken cancellationToken = default)` starts the host, waits for shutdown, and completes after the host has stopped.
- The inherited `IHost` members expose the host identity, context, start, stop, and disposal lifecycle.

The Hosting implementation registers the protocol endpoint after services explicitly added through
`IConfigurationStoreApplicationBuilder`. A token that is already cancelled still drives the host
through a clean start-and-stop transition; cancellation after startup requests graceful shutdown.

## Exceptions

`RunAsync` throws `ObjectDisposedException` when invoked after the application has been disposed. Lifecycle failures are propagated to the caller.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;

await using IConfigurationStoreApplication application =
    ConfigurationStoreApplication.CreateBuilder(args).Build();

await application.RunAsync(cancellationToken);
```
