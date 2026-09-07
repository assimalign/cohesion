# Assimalign.Cohesion.LoadBalancer

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the LoadBalancer area. The implementation and creation entry point live in `Assimalign.Cohesion.LoadBalancer.Hosting`.

## Public surface

- `ILoadBalancerApplicationBuilder` extends the shared host-builder contract and builds an `ILoadBalancerApplication`.
- `ILoadBalancerApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Load-balancing behavior is outside this slice.
