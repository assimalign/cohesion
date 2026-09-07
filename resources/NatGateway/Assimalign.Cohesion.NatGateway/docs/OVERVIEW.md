# Assimalign.Cohesion.NatGateway

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the NatGateway area. The implementation and creation entry point live in `Assimalign.Cohesion.NatGateway.Hosting`.

## Public surface

- `INatGatewayApplicationBuilder` extends the shared host-builder contract and builds an `INatGatewayApplication`.
- `INatGatewayApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. NAT-gateway behavior is outside this slice.
