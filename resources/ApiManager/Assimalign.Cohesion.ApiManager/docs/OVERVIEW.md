# Assimalign.Cohesion.ApiManager

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the ApiManager area. The implementation and creation entry point live in `Assimalign.Cohesion.ApiManager.Hosting`.

## Public surface

- `IApiManagerApplicationBuilder` extends the shared host-builder contract and builds an `IApiManagerApplication`.
- `IApiManagerApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. API management behavior is outside this slice.
