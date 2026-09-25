# IIoTHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IoTHub`
Assembly: `Assimalign.Cohesion.IoTHub`

## Purpose

`IIoTHubApplicationBuilder` is the public composition seam for an IoT hub application. It exposes `Build()` returning `IIoTHubApplication`.

## Surface and behavior

- `Build()` creates a configured IoT hub application.

The current filler builder has no area feature or service registrations by default. Services registered on the concrete Hosting builder start in registration order and stop in reverse order. The public concrete `IoTHubApplicationBuilder` lives in `Assimalign.Cohesion.IoTHub.Hosting`.

## Exceptions

The concrete Hosting builder's `AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.IoTHub;
using Assimalign.Cohesion.IoTHub.Hosting;

IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
await using IoTHubApplication application = builder.Build();
```

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIoTHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIoTHubApplicationContext` exposes `ContentRootPath`. `IIoTHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IoTHubApplication.CreateBuilder(args)` returns the public concrete `IoTHubApplicationBuilder`; its `Build()` returns the public `IoTHubApplication : Host<IoTHubApplicationContext>`. The public `IoTHubApplicationContext` implements `IIoTHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
