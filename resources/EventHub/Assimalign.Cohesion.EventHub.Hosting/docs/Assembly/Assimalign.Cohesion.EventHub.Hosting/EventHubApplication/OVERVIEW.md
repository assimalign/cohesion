# EventHubApplication

Namespace: `Assimalign.Cohesion.EventHub.Hosting`
Assembly: `Assimalign.Cohesion.EventHub.Hosting`

## Purpose

`EventHubApplication` is the public factory for the event hub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IEventHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.EventHub;
using Assimalign.Cohesion.EventHub.Hosting;

IEventHubApplicationBuilder builder = EventHubApplication.CreateBuilder(args);
await using IEventHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
