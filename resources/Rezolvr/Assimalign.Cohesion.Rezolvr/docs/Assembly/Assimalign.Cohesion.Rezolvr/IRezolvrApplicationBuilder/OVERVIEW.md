# IRezolvrApplicationBuilder

Namespace: `Assimalign.Cohesion.Rezolvr`
Assembly: `Assimalign.Cohesion.Rezolvr`

## Purpose

`IRezolvrApplicationBuilder` is the public composition seam for a Rezolvr application. It exposes `Build()` returning `IRezolvrApplication`.

## Surface and behavior

- `Build()` creates a configured Rezolvr application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The public concrete `RezolvrApplicationBuilder` lives in `Assimalign.Cohesion.Rezolvr.Hosting`.

## Exceptions

The concrete Hosting builder's `AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

RezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);
await using RezolvrApplication application = builder.Build();
```

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IRezolvrApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IRezolvrApplicationContext` exposes `ContentRootPath`. `IRezolvrApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`RezolvrApplication.CreateBuilder(args)` returns the public concrete `RezolvrApplicationBuilder`; its `Build()` returns the public `RezolvrApplication : Host<RezolvrApplicationContext>`. The public `RezolvrApplicationContext` implements `IRezolvrApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
