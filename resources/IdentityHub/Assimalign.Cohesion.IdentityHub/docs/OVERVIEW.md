# Assimalign.Cohesion.IdentityHub

## Summary

Defines the public code-first composition and lifecycle contracts for an IdentityHub resource. The concrete issuer is supplied by `Assimalign.Cohesion.IdentityHub.Hosting`.

## Public surface

- `AddAudience(string)` declares an exact access-token audience.
- `AddClient(string, Action<IdentityHubClientOptions>)` registers client credentials, device authorization, token lifetime, and allowed audiences.
- `AddService(...)` composes additional host lifecycle services.
- `Build()` produces an `IIdentityHubApplication`, which exposes the standard host lifecycle and `RunAsync`.

At least one grant and one declared audience are required for every registered client. Gateway command descriptors ship in IdentityHub.ApplicationModel; the runtime code-first verbs ship here. Hosting honors both registration paths through the same token issuer.
