# Assimalign.Cohesion.VpnGateway

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the VpnGateway area. The implementation and creation entry point live in `Assimalign.Cohesion.VpnGateway.Hosting`.

## Public surface

- `IVpnGatewayApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `IVpnGatewayApplication`.
- `IVpnGatewayApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; VPN data-plane behavior remains outside this slice.
