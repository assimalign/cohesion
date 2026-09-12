# ApiManagerApplication

Namespace: `Assimalign.Cohesion.ApiManager.Hosting`
Assembly: `Assimalign.Cohesion.ApiManager.Hosting`

## Purpose

`ApiManagerApplication` is the public factory for the API manager hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IApiManagerApplicationBuilder`.
- The entry assembly selects the generated control-plane registration. Enabled builders capture ResourceRuntime.Current; command-line arguments remain available for future domain composition.
- Building the returned builder materializes its explicitly registered host services in registration order; enabled resources additionally register their private control-plane listener when its ambient endpoint is present.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.ApiManager.Hosting;

IApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder(args);
await using IApiManagerApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

RunAsync delegates to the shared host runner. Namespaced management authenticates gateway-issued ES256 tokens; bare probes remain public. Without a registration the host creates no listener. Invalid managed identity or endpoint configuration fails during Build().
