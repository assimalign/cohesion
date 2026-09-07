# IIoTHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IoTHub`
Assembly: `Assimalign.Cohesion.IoTHub`

## Purpose

`IIoTHubApplicationBuilder` is the public composition seam for an IoT hub application. It extends `IHostBuilder` while refining `Build()` to return `IIoTHubApplication`.

## Surface and behavior

- `Build()` creates a configured IoT hub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.IoTHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.IoTHub;
using Assimalign.Cohesion.IoTHub.Hosting;

IIoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
await using IIoTHubApplication application = builder.Build();
```
