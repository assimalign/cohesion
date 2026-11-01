# Assimalign.Cohesion.IdentityHub.Hosting

## Summary

Provides `IdentityHubApplication.CreateBuilder(args)` and the public concrete application, builder,
and context for the AOT-safe OpenID Connect issuer runtime. Issuer services remain internal.

The host serves discovery, persisted ES256 JWKS, client-credentials tokens, and public health probes. Configuration is code-first through `AddAudience` and `AddClient`. Only `openid` and an empty scope are accepted.

The retired resource integration was removed on 2026-10-31 with the application-model restart (assimalign/cohesion#1759), and the area's resource integration is `Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel`, which references this module; this module references no application-model library.

Device authorization and its fixed-subject browser approval page are exposed only by a loopback Local endpoint. Applications needing account login, subject selection, consent, recovery, or federation compose an authenticated user-facing flow separately.

Self-signed TLS is limited to loopback Local; non-Local HTTPS fails closed rather than generating production TLS material.

## Concrete composition (T10 / O34)

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `IdentityHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<IdentityHubApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.

## Dependency injection composition

`IdentityHubApplicationBuilder.Services` holds every registration the application runs with. `AddService` registers an `IHostService`, and a direct `IHostService` registration joins the lifecycle in the same order. `Build` closes registration and resolves the services once. Disposing the application disposes every factory-created service. See [DESIGN.md](DESIGN.md#dependency-injection-composition).
