# IEventHubApplicationBuilder

Namespace: `Assimalign.Cohesion.EventHub`
Assembly: `Assimalign.Cohesion.EventHub`

## Purpose

`IEventHubApplicationBuilder` is the public composition seam for an event hub application. It extends `IHostBuilder` while refining `Build()` to return `IEventHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured event hub application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.EventHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.EventHub;
using Assimalign.Cohesion.EventHub.Hosting;

IEventHubApplicationBuilder builder = EventHubApplication.CreateBuilder(args);
await using IEventHubApplication application = builder.Build();
```
