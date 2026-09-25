# Assimalign.Cohesion.LogSpace

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the LogSpace area. The implementation and creation entry point live in `Assimalign.Cohesion.LogSpace.Hosting`.

## Public surface

- `ILogSpaceApplicationBuilder` owns area declarations and builds an `ILogSpaceApplication`.
- `ILogSpaceApplication` exposes `Context`, `StartAsync`, and `StopAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the concrete Hosting builder; log-storage behavior is outside this slice.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `ILogSpaceApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ILogSpaceApplicationContext` exposes `ContentRootPath`. `ILogSpaceApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`LogSpaceApplication.CreateBuilder(args)` returns the public concrete `LogSpaceApplicationBuilder`; its `Build()` returns the public `LogSpaceApplication : Host<LogSpaceApplicationContext>`. The public `LogSpaceApplicationContext` implements `ILogSpaceApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
