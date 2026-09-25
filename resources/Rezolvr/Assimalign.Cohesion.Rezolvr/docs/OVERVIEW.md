# Assimalign.Cohesion.Rezolvr

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the Rezolvr area. The implementation and creation entry point live in `Assimalign.Cohesion.Rezolvr.Hosting`.

## Public surface

- `IRezolvrApplicationBuilder` owns area declarations and builds an `IRezolvrApplication`.
- `IRezolvrApplication` exposes `Context`, `StartAsync`, and `StopAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; Rezolvr remains a standalone DNS server product, and DNS behavior is outside this slice.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IRezolvrApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IRezolvrApplicationContext` exposes `ContentRootPath`. `IRezolvrApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`RezolvrApplication.CreateBuilder(args)` returns the public concrete `RezolvrApplicationBuilder`; its `Build()` returns the public `RezolvrApplication : Host<RezolvrApplicationContext>`. The public `RezolvrApplicationContext` implements `IRezolvrApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
