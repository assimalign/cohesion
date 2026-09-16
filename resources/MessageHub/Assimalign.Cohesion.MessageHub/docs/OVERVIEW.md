# Assimalign.Cohesion.MessageHub

## Summary

This project defines the public, contract-only builder and application lifecycle seam for the MessageHub area. The implementation and creation entry point live in `Assimalign.Cohesion.MessageHub.Hosting`.

## Public surface

- `IMessageHubApplicationBuilder` owns area declarations and builds an `IMessageHubApplication`.
- `IMessageHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`.

The current application is a composition-only filler that is empty by default. Caller-registered services participate in the shared ordered lifecycle; message-broker behavior remains outside this slice.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IMessageHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IMessageHubApplicationContext` exposes `ContentRootPath`. `IMessageHubApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`MessageHubApplication.CreateBuilder(args)` returns the public concrete `MessageHubApplicationBuilder`; its `Build()` returns the public `MessageHubApplication : Host<MessageHubApplicationContext>`. The public `MessageHubApplicationContext` implements `IMessageHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
