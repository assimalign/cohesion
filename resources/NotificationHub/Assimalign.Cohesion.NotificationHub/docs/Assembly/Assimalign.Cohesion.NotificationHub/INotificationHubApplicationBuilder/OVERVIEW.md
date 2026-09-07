# INotificationHubApplicationBuilder

Namespace: `Assimalign.Cohesion.NotificationHub`
Assembly: `Assimalign.Cohesion.NotificationHub`

## Purpose

`INotificationHubApplicationBuilder` is the public composition seam for a NotificationHub application. It extends `IHostBuilder` while refining `Build()` to return `INotificationHubApplication`.

## Surface and behavior

- `Build()` creates a configured NotificationHub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.NotificationHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.NotificationHub;
using Assimalign.Cohesion.NotificationHub.Hosting;

INotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder(args);
await using INotificationHubApplication application = builder.Build();
```
