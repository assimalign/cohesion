# IEmailHubApplicationBuilder

Namespace: `Assimalign.Cohesion.EmailHub`
Assembly: `Assimalign.Cohesion.EmailHub`

## Purpose

`IEmailHubApplicationBuilder` is the public composition seam for an email hub application. It extends `IHostBuilder` while refining `Build()` to return `IEmailHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured email hub application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.EmailHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.EmailHub;
using Assimalign.Cohesion.EmailHub.Hosting;

IEmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
await using IEmailHubApplication application = builder.Build();
```
