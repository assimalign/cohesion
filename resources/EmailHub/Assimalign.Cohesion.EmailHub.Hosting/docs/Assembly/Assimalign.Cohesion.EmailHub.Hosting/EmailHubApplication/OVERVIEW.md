# EmailHubApplication

Namespace: `Assimalign.Cohesion.EmailHub.Hosting`
Assembly: `Assimalign.Cohesion.EmailHub.Hosting`

## Purpose

`EmailHubApplication` is the public factory for the email hub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IEmailHubApplicationBuilder`.
- The entry assembly selects the generated control-plane registration. Enabled builders capture ResourceRuntime.Current; command-line arguments remain available for future domain composition.
- Building the returned builder materializes its explicitly registered host services in registration order; enabled resources additionally register their private control-plane listener when its ambient endpoint is present.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.EmailHub;
using Assimalign.Cohesion.EmailHub.Hosting;

IEmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
await using IEmailHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

RunAsync delegates to the shared host runner. Namespaced management authenticates gateway-issued ES256 tokens; bare probes remain public. Without a registration the host creates no listener. Invalid managed identity or endpoint configuration fails during Build().
