# Assimalign.Cohesion.IoTHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the IoTHub area. The implementation and creation entry point live in `Assimalign.Cohesion.IoTHub.Hosting`.

## Public surface

- `IIoTHubApplicationBuilder` owns area declarations and builds an `IIoTHubApplication`.
- `IIoTHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`.

The current application remains a filler with no area behavior or hosted services registered by default. Consumers can add explicit lifecycle services through the concrete Hosting builder; IoT-hub behavior is outside this slice.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIoTHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIoTHubApplicationContext` exposes `ContentRootPath`. `IIoTHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IoTHubApplication.CreateBuilder(args)` returns the public concrete `IoTHubApplicationBuilder`; its `Build()` returns the public `IoTHubApplication : Host<IoTHubApplicationContext>`. The public `IoTHubApplicationContext` implements `IIoTHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
