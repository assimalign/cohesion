# INotificationHubApplicationBuilder

Namespace: `Assimalign.Cohesion.NotificationHub`
Assembly: `Assimalign.Cohesion.NotificationHub`

## Purpose

`INotificationHubApplicationBuilder` is the public composition seam for a NotificationHub application. It extends `IHostBuilder` while refining `Build()` to return `INotificationHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new NotificationHub context.
- `Build()` creates a configured NotificationHub application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.NotificationHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.NotificationHub;
using Assimalign.Cohesion.NotificationHub.Hosting;

INotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder(args);
await using INotificationHubApplication application = builder.Build();
```
