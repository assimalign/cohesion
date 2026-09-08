# IRezolvrApplicationBuilder

Namespace: `Assimalign.Cohesion.Rezolvr`
Assembly: `Assimalign.Cohesion.Rezolvr`

## Purpose

`IRezolvrApplicationBuilder` is the public composition seam for a Rezolvr application. It extends `IHostBuilder` while refining `Build()` to return `IRezolvrApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new Rezolvr context.
- `Build()` creates a configured Rezolvr application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.Rezolvr.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

IRezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);
await using IRezolvrApplication application = builder.Build();
```
