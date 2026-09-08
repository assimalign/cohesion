# IMediaHubApplicationBuilder

Namespace: `Assimalign.Cohesion.MediaHub`
Assembly: `Assimalign.Cohesion.MediaHub`

## Purpose

`IMediaHubApplicationBuilder` is the public composition seam for a MediaHub application. It extends `IHostBuilder` while refining `Build()` to return `IMediaHubApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new MediaHub context.
- `Build()` creates a configured MediaHub application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.MediaHub.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.MediaHub;
using Assimalign.Cohesion.MediaHub.Hosting;

IMediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
await using IMediaHubApplication application = builder.Build();
```
