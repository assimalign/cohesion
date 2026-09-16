# EmailHub

EmailHub is the L3 service platform intended to compose email messages and templates, select transports, track delivery, and apply suppression policy.

The host supports enabled-resource control planes; domain services remain fillers pending the area program.

## Projects

- `Assimalign.Cohesion.EmailHub` defines the public area-root application and builder contracts alongside the existing email-hub abstraction.
- `Assimalign.Cohesion.EmailHub.Hosting` provides the concrete creation entry point and host lifecycle with explicit `IHostService` registration.

- `Assimalign.Cohesion.EmailHub.ApplicationModel` supplies the typed manifest, planner, descriptor, and default control-plane factory as a NuGet-only package.

## Layering and dependencies

As an L3 service platform, EmailHub composes the L2 `Assimalign.Cohesion.Hosting` runtime rather than defining its own host lifecycle. Hosting is built on the L1 `Assimalign.Cohesion.Core` foundation; Hosting privately composes Web.Hosting.Resources, Web.Hosting, HTTP, and TCP for its resource listener.

## Project documentation

- [Root overview](./Assimalign.Cohesion.EmailHub/docs/OVERVIEW.md)
- [Root design](./Assimalign.Cohesion.EmailHub/docs/DESIGN.md)
- [Hosting overview](./Assimalign.Cohesion.EmailHub.Hosting/docs/OVERVIEW.md)
- [Hosting design](./Assimalign.Cohesion.EmailHub.Hosting/docs/DESIGN.md)

## Application composition (O34)

The root contracts are hosting-free (O34): `IEmailHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IEmailHubApplicationContext` exposes `ContentRootPath`. `IEmailHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`EmailHubApplication.CreateBuilder(args)` returns the public concrete `EmailHubApplicationBuilder`; its `Build()` returns the public `EmailHubApplication : Host<EmailHubApplicationContext>`. The public `EmailHubApplicationContext` implements `IEmailHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

```csharp
using Assimalign.Cohesion.EmailHub.Hosting;

EmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using EmailHubApplication application = builder.Build();
await application.RunAsync();
```
