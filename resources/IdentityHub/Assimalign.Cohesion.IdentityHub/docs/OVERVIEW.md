# Assimalign.Cohesion.IdentityHub

## Summary

Defines the public code-first composition and lifecycle contracts for an IdentityHub resource. The concrete issuer is supplied by `Assimalign.Cohesion.IdentityHub.Hosting`.

## Public surface

- `AddAudience(string)` declares an exact access-token audience.
- `AddClient(string, Action<IdentityHubClientOptions>)` registers client credentials, device authorization, token lifetime, and allowed audiences.
- `Build()` produces an `IIdentityHubApplication`, which exposes `Context`, `StartAsync`, and `StopAsync`.

At least one grant and one declared audience are required for every registered client. Gateway command descriptors ship in IdentityHub.ApplicationModel; the runtime code-first verbs ship here. Hosting honors both registration paths through the same token issuer.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIdentityHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIdentityHubApplicationContext` exposes `ContentRootPath`. `IIdentityHubApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
