# EventHubApplication

Namespace: `Assimalign.Cohesion.EventHub.Hosting`
Assembly: `Assimalign.Cohesion.EventHub.Hosting`

## Purpose

`EventHubApplication` is the public factory for the event hub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IEventHubApplicationBuilder`.
- The entry assembly selects the generated control-plane registration. Enabled builders capture ResourceRuntime.Current; command-line arguments remain available for future domain composition.
- Building the returned builder materializes its explicitly registered host services in registration order; enabled resources additionally register their private control-plane listener when its ambient endpoint is present.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.EventHub;
using Assimalign.Cohesion.EventHub.Hosting;

IEventHubApplicationBuilder builder = EventHubApplication.CreateBuilder(args);
await using IEventHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

RunAsync delegates to the shared host runner. Namespaced management authenticates gateway-issued ES256 tokens; bare probes remain public. Without a registration the host creates no listener. Invalid managed identity or endpoint configuration fails during Build().
