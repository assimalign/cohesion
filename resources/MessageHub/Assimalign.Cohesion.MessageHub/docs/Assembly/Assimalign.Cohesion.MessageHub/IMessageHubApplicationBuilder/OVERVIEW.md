# IMessageHubApplicationBuilder

Namespace: `Assimalign.Cohesion.MessageHub`
Assembly: `Assimalign.Cohesion.MessageHub`

## Purpose

`IMessageHubApplicationBuilder` is the public composition seam for a MessageHub application. It extends `IHostBuilder` while refining `Build()` to return `IMessageHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new MessageHub context.
- `Build()` creates a configured MessageHub application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.MessageHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.MessageHub;
using Assimalign.Cohesion.MessageHub.Hosting;

IMessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
await using IMessageHubApplication application = builder.Build();
```
