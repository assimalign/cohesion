# IMessageHubApplication

Namespace: `Assimalign.Cohesion.MessageHub`
Assembly: `Assimalign.Cohesion.MessageHub`

## Purpose and surface

The root contracts are hosting-free (O34): `IMessageHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IMessageHubApplicationContext` exposes `ContentRootPath`. `IMessageHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`StartAsync(CancellationToken)` starts the application; `StopAsync(CancellationToken)` drains and stops it. Lifecycle failures propagate to the caller. The contract carries no host identity, runner, or disposal members.

## Hosting implementation

`MessageHubApplication.CreateBuilder(args)` returns the public concrete `MessageHubApplicationBuilder`; its `Build()` returns the public `MessageHubApplication : Host<MessageHubApplicationContext>`. The public `MessageHubApplicationContext` implements `IMessageHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Usage

```csharp
using Assimalign.Cohesion.MessageHub.Hosting;

MessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using MessageHubApplication application = builder.Build();
await application.RunAsync();
```
