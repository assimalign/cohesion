# Assimalign.Cohesion.VpnGateway

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the VpnGateway area. The implementation and creation entry point live in `Assimalign.Cohesion.VpnGateway.Hosting`.

## Public surface

- `IVpnGatewayApplicationBuilder` owns area declarations and builds an `IVpnGatewayApplication`.
- `IVpnGatewayApplication` exposes `Context`, `StartAsync`, and `StopAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; VPN data-plane behavior remains outside this slice.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IVpnGatewayApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IVpnGatewayApplicationContext` exposes `ContentRootPath`. `IVpnGatewayApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`VpnGatewayApplication.CreateBuilder(args)` returns the public concrete `VpnGatewayApplicationBuilder`; its `Build()` returns the public `VpnGatewayApplication : Host<VpnGatewayApplicationContext>`. The public `VpnGatewayApplicationContext` implements `IVpnGatewayApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
