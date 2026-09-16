# MessageHub

MessageHub is the L3 messaging service platform intended to provide queues, topics, producers, consumers, settlement, retries, dead-lettering, and message correlation.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.MessageHub` defines the public area-root application and builder contracts alongside the existing message-hub abstraction.
- `Assimalign.Cohesion.MessageHub.Hosting` provides the concrete creation entry point and the caller-configurable host-service lifecycle.
- `Assimalign.Cohesion.MessageHub.Client` contains the existing client-side contract surface and remains an early implementation.

- `Assimalign.Cohesion.MessageHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, MessageHub composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.MessageHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.MessageHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.MessageHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.MessageHub.Hosting/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `IMessageHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IMessageHubApplicationContext` exposes `ContentRootPath`. `IMessageHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`MessageHubApplication.CreateBuilder(args)` returns the public concrete `MessageHubApplicationBuilder`; its `Build()` returns the public `MessageHubApplication : Host<MessageHubApplicationContext>`. The public `MessageHubApplicationContext` implements `IMessageHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.MessageHub.Hosting;

MessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using MessageHubApplication application = builder.Build();
await application.RunAsync();
```
