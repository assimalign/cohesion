# Assimalign.Cohesion.IdentityHub Design

## Application seam

The area root owns the public contracts composed by an IdentityHub resource. `IIdentityHubApplicationBuilder` declares token audiences with `AddAudience`, registers OAuth clients with `AddClient`, and builds `IIdentityHubApplication`. The public concrete application, builder, and context live in `Assimalign.Cohesion.IdentityHub.Hosting`.

Client registration is intentionally code-first. A non-empty `ClientSecret` enables `client_credentials`; `AllowDeviceAuthorization` enables the device grant. Clients explicitly list the audiences they may request, and build fails if a client names an undeclared audience or enables no grant. Corresponding declarative gateway commands now ship in IdentityHub.ApplicationModel; Hosting combines their durable registry with these builder registrations.

## Security boundary

The public options carry a client secret only during composition. Hosting snapshots options and retains only a SHA-256 digest, compared in fixed time. Access-token lifetimes are positive and capped at 24 hours. IdentityHub builds protocol tokens on the shared IdentityModel and JWT contracts instead of defining competing token, claim, subject, credential, or session primitives.

## Hosting isolation and AOT

The root references Core and IdentityModel contracts, with no Hosting library reference. HTTP serving, persistence, cryptography, and ResourceRuntime integration belong to the Hosting module. The surface requires no runtime scanning, dynamic code generation, or container activation and remains trimming- and NativeAOT-safe.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIdentityHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIdentityHubApplicationContext` exposes `ContentRootPath`. `IIdentityHubApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
