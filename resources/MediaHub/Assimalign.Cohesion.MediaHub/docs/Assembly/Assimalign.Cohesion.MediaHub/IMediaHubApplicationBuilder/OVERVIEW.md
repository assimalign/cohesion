# IMediaHubApplicationBuilder

Namespace: `Assimalign.Cohesion.MediaHub`
Assembly: `Assimalign.Cohesion.MediaHub`

## Purpose

`IMediaHubApplicationBuilder` is the public composition seam for a MediaHub application. It exposes `Build()` returning `IMediaHubApplication`.

## Surface and behavior

- `Build()` creates a configured MediaHub application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The public concrete `MediaHubApplicationBuilder` lives in `Assimalign.Cohesion.MediaHub.Hosting`.

## Exceptions

The concrete Hosting builder's `AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.MediaHub;
using Assimalign.Cohesion.MediaHub.Hosting;

MediaHubApplicationBuilder builder = MediaHubApplication.CreateBuilder(args);
await using MediaHubApplication application = builder.Build();
```

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IMediaHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IMediaHubApplicationContext` exposes `ContentRootPath`. `IMediaHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`MediaHubApplication.CreateBuilder(args)` returns the public concrete `MediaHubApplicationBuilder`; its `Build()` returns the public `MediaHubApplication : Host<MediaHubApplicationContext>`. The public `MediaHubApplicationContext` implements `IMediaHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
