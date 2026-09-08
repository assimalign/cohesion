# IIoTHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IoTHub`
Assembly: `Assimalign.Cohesion.IoTHub`

## Purpose

`IIoTHubApplicationBuilder` is the public composition seam for an IoT hub application. It extends `IHostBuilder` while refining `Build()` to return `IIoTHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured IoT hub application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.IoTHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.IoTHub;
using Assimalign.Cohesion.IoTHub.Hosting;

IIoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
await using IIoTHubApplication application = builder.Build();
```
