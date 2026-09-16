# IoTHubApplication

Namespace: `Assimalign.Cohesion.IoTHub.Hosting`
Assembly: `Assimalign.Cohesion.IoTHub.Hosting`

## Purpose

`IoTHubApplication` is the public concrete application and creation entry point for the IoT hub hosting module. The application, builder, and context are public; runtime options remain internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns a `IoTHubApplicationBuilder`.
- The entry assembly selects the generated control-plane registration. Enabled builders capture ResourceRuntime.Current; command-line arguments remain available for future domain composition.
- Building the returned builder materializes its explicitly registered host services in registration order; enabled resources additionally register their private control-plane listener when its ambient endpoint is present.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.IoTHub;
using Assimalign.Cohesion.IoTHub.Hosting;

IoTHubApplicationBuilder builder = IoTHubApplication.CreateBuilder(args);
await using IoTHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```

RunAsync delegates to the shared host runner. Namespaced management authenticates gateway-issued ES256 tokens; bare probes remain public. Without a registration the host creates no listener. Invalid managed identity or endpoint configuration fails during Build().

## Concrete composition (T10 / O34)

`IoTHubApplication.CreateBuilder(args)` returns the public concrete `IoTHubApplicationBuilder`; its `Build()` returns the public `IoTHubApplication : Host<IoTHubApplicationContext>`. The public `IoTHubApplicationContext` implements `IIoTHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `IoTHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<IoTHubApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.
