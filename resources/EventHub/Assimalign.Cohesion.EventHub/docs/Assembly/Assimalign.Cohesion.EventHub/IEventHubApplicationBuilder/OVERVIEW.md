# IEventHubApplicationBuilder

Namespace: `Assimalign.Cohesion.EventHub`
Assembly: `Assimalign.Cohesion.EventHub`

## Purpose

`IEventHubApplicationBuilder` is the public composition seam for an event hub application. It extends `IHostBuilder` while refining `Build()` to return `IEventHubApplication`.

## Surface and behavior

- `Build()` creates a configured event hub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.EventHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.EventHub;
using Assimalign.Cohesion.EventHub.Hosting;

IEventHubApplicationBuilder builder = EventHubApplication.CreateBuilder(args);
await using IEventHubApplication application = builder.Build();
```
