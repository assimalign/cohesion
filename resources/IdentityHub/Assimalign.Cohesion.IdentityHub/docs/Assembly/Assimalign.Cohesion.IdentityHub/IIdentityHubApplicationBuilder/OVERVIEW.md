# IIdentityHubApplicationBuilder

Namespace: `Assimalign.Cohesion.IdentityHub`
Assembly: `Assimalign.Cohesion.IdentityHub`

## Purpose

`IIdentityHubApplicationBuilder` is the public composition seam for an identity hub application. It extends `IHostBuilder` while refining `Build()` to return `IIdentityHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured identity hub application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.IdentityHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

IIdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);
await using IIdentityHubApplication application = builder.Build();
```
