# MediaHubApplication

Namespace: `Assimalign.Cohesion.MediaHub.Hosting`
Assembly: `Assimalign.Cohesion.MediaHub.Hosting`

## Purpose

`MediaHubApplication` is the public factory for the MediaHub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IMediaHubApplicationBuilder`.
- The entry assembly selects the generated control-plane registration. Enabled builders capture ResourceRuntime.Current; command-line arguments remain available for future domain composition.
- Building the returned builder materializes its registered service factories once against the new MediaHub context and preserves registration order; an enabled resource also registers its private control-plane listener when its ambient endpoint is present.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.MediaHub;
using Assimalign.Cohesion.MediaHub.Hosting;

IMediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
await using IMediaHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

RunAsync delegates to the shared host runner. Namespaced management authenticates gateway-issued ES256 tokens; bare probes remain public. Without a registration the host creates no listener. Invalid managed identity or endpoint configuration fails during Build().
