# Assimalign.Cohesion.Rezolvr

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the Rezolvr area. The implementation and creation entry point live in `Assimalign.Cohesion.Rezolvr.Hosting`.

## Public surface

- `IRezolvrApplicationBuilder` extends the shared host-builder contract and builds an `IRezolvrApplication`.
- `IRezolvrApplication` exposes the shared host lifecycle plus `RunAsync`.

The current application is an empty filler that exists to make the area SDK and shared framework consumable. Rezolvr remains a standalone DNS server product; DNS behavior is outside this slice.
