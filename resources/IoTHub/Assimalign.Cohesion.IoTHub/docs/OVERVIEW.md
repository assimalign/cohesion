# Assimalign.Cohesion.IoTHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the IoTHub area. The implementation and creation entry point live in `Assimalign.Cohesion.IoTHub.Hosting`.

## Public surface

- `IIoTHubApplicationBuilder` extends the shared host-builder contract and builds an `IIoTHubApplication`.
- `IIoTHubApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. IoT-hub behavior is outside this slice.
