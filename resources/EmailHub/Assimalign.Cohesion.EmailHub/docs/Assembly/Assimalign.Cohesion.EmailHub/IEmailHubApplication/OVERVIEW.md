# IEmailHubApplication

Namespace: `Assimalign.Cohesion.EmailHub`
Assembly: `Assimalign.Cohesion.EmailHub`

## Purpose and surface

The root contracts are hosting-free (O34): `IEmailHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IEmailHubApplicationContext` exposes `ContentRootPath`. `IEmailHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`StartAsync(CancellationToken)` starts the application; `StopAsync(CancellationToken)` drains and stops it. Lifecycle failures propagate to the caller. The contract carries no host identity, runner, or disposal members.

## Hosting implementation

`EmailHubApplication.CreateBuilder(args)` returns the public concrete `EmailHubApplicationBuilder`; its `Build()` returns the public `EmailHubApplication : Host<EmailHubApplicationContext>`. The public `EmailHubApplicationContext` implements `IEmailHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Usage

```csharp
using Assimalign.Cohesion.EmailHub.Hosting;

EmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using EmailHubApplication application = builder.Build();
await application.RunAsync();
```
