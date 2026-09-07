# MessageHubApplication

Namespace: `Assimalign.Cohesion.MessageHub.Hosting`
Assembly: `Assimalign.Cohesion.MessageHub.Hosting`

## Purpose

`MessageHubApplication` is the public factory for the MessageHub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IMessageHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.MessageHub;
using Assimalign.Cohesion.MessageHub.Hosting;

IMessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
await using IMessageHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
