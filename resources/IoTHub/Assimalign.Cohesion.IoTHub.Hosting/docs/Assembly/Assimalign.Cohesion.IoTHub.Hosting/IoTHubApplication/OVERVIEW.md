# IoTHubApplication

Namespace: `Assimalign.Cohesion.IoTHub.Hosting`
Assembly: `Assimalign.Cohesion.IoTHub.Hosting`

## Purpose

`IoTHubApplication` is the public factory for the IoT hub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IIoTHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its explicitly registered host services in registration order; no services are registered automatically.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.IoTHub;
using Assimalign.Cohesion.IoTHub.Hosting;

IIoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
await using IIoTHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
