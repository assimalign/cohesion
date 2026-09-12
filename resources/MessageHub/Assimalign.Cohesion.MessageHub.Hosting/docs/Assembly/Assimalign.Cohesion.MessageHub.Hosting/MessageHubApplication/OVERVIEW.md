# MessageHubApplication

Namespace: `Assimalign.Cohesion.MessageHub.Hosting`
Assembly: `Assimalign.Cohesion.MessageHub.Hosting`

## Purpose

`MessageHubApplication` is the public factory for the MessageHub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IMessageHubApplicationBuilder`.
- The entry assembly selects the generated control-plane registration. Enabled builders capture ResourceRuntime.Current; command-line arguments remain available for future domain composition.
- Building the returned builder materializes its registered service factories once against the new MessageHub context and preserves registration order; an enabled resource also registers its private control-plane listener when its ambient endpoint is present.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.MessageHub;
using Assimalign.Cohesion.MessageHub.Hosting;

IMessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
await using IMessageHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

RunAsync delegates to the shared host runner. Namespaced management authenticates gateway-issued ES256 tokens; bare probes remain public. Without a registration the host creates no listener. Invalid managed identity or endpoint configuration fails during Build().
