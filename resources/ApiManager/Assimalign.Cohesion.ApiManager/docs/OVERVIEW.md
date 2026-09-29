# Assimalign.Cohesion.ApiManager

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the ApiManager area. The implementation and creation entry point live in `Assimalign.Cohesion.ApiManager.Hosting`.

## Public surface

- `IApiManagerApplicationBuilder` owns area declarations and builds an `IApiManagerApplication`.
- `IApiManagerApplication` exposes `Context`, `StartAsync`, and `StopAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the concrete Hosting builder; API management behavior is outside this slice.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IApiManagerApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IApiManagerApplicationContext` exposes `ContentRootPath`. `IApiManagerApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`ApiManagerApplication.CreateBuilder(args)` returns the public concrete `ApiManagerApplicationBuilder`; its `Build()` returns the public `ApiManagerApplication : Host<ApiManagerApplicationContext>`. The public `ApiManagerApplicationContext` implements `IApiManagerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
