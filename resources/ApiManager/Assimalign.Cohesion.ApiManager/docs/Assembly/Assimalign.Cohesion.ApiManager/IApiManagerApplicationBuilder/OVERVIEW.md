# IApiManagerApplicationBuilder

Namespace: `Assimalign.Cohesion.ApiManager`
Assembly: `Assimalign.Cohesion.ApiManager`

## Purpose

`IApiManagerApplicationBuilder` is the public composition seam for an API manager application. It extends `IHostBuilder` while refining `Build()` to return `IApiManagerApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured API manager application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.ApiManager.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.ApiManager;
using Assimalign.Cohesion.ApiManager.Hosting;

IApiManagerApplicationBuilder builder = ApiManagerApplication.CreateBuilder(args);
await using IApiManagerApplication application = builder.Build();
```
