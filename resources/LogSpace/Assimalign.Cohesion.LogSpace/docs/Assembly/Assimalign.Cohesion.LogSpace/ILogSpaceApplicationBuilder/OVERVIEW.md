# ILogSpaceApplicationBuilder

Namespace: `Assimalign.Cohesion.LogSpace`
Assembly: `Assimalign.Cohesion.LogSpace`

## Purpose

`ILogSpaceApplicationBuilder` is the public composition seam for a LogSpace application. It extends `IHostBuilder` while refining `Build()` to return `ILogSpaceApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured LogSpace application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.LogSpace.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting;

ILogSpaceApplicationBuilder builder = LogSpaceApplication.CreateBuilder(args);
await using ILogSpaceApplication application = builder.Build();
```
