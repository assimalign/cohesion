# IMessageHubApplicationBuilder

Namespace: `Assimalign.Cohesion.MessageHub`
Assembly: `Assimalign.Cohesion.MessageHub`

## Purpose

`IMessageHubApplicationBuilder` is the public composition seam for a MessageHub application. It extends `IHostBuilder` while refining `Build()` to return `IMessageHubApplication`.

## Surface and behavior

- `Build()` creates a configured MessageHub application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.MessageHub.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.MessageHub;
using Assimalign.Cohesion.MessageHub.Hosting;

IMessageHubApplicationBuilder builder = MessageHubApplication.CreateBuilder(args);
await using IMessageHubApplication application = builder.Build();
```
