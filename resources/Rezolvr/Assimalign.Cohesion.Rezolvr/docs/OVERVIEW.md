# Assimalign.Cohesion.Rezolvr

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the Rezolvr area. The implementation and creation entry point live in `Assimalign.Cohesion.Rezolvr.Hosting`.

## Public surface

- `IRezolvrApplicationBuilder` extends the shared host-builder contract, registers host-service instances or context-aware factories, and builds an `IRezolvrApplication`.
- `IRezolvrApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; Rezolvr remains a standalone DNS server product, and DNS behavior is outside this slice.
