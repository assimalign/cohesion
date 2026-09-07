# NotificationHubApplication

Namespace: `Assimalign.Cohesion.NotificationHub.Hosting`
Assembly: `Assimalign.Cohesion.NotificationHub.Hosting`

## Purpose

`NotificationHubApplication` is the public factory for the NotificationHub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `INotificationHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.NotificationHub;
using Assimalign.Cohesion.NotificationHub.Hosting;

INotificationHubApplicationBuilder builder = NotificationHubApplication.CreateBuilder(args);
await using INotificationHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
