# Assimalign.Cohesion.NatGateway

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the NatGateway area. The implementation and creation entry point live in `Assimalign.Cohesion.NatGateway.Hosting`.

## Public surface

- `INatGatewayApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `INatGatewayApplication`.
- `INatGatewayApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; NAT-gateway behavior remains outside this slice.
