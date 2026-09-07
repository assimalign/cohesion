# Assimalign.Cohesion.VpnGateway

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the VpnGateway area. The implementation and creation entry point live in `Assimalign.Cohesion.VpnGateway.Hosting`.

## Public surface

- `IVpnGatewayApplicationBuilder` extends the shared host-builder contract and builds an `IVpnGatewayApplication`.
- `IVpnGatewayApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. VPN data-plane behavior is outside this slice.
