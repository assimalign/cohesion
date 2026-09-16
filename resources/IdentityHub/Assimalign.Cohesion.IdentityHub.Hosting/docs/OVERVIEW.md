# Assimalign.Cohesion.IdentityHub.Hosting

## Summary

Provides `IdentityHubApplication.CreateBuilder(args)` and the public concrete application, builder,
and context for the AOT-safe OpenID Connect issuer runtime. Issuer services remain internal.

The host serves discovery, persisted ES256 JWKS, client-credentials tokens, public health probes, and the bootstrap-authenticated Cohesion resource control plane. Configuration is code-first through `AddAudience` and `AddClient`; the generated resource manifest supplies the `https` endpoint and `data` volume. Only `openid` and an empty scope are accepted.

Device authorization and its fixed-subject browser approval page are exposed only by a loopback Local endpoint. Applications needing account login, subject selection, consent, recovery, or federation compose an authenticated user-facing flow separately.

HTTPS reads a PEM certificate, private key, and optional chain from a materialized `tls` mount. Without that mount, self-signed TLS is limited to loopback Local; non-Local HTTPS fails closed rather than generating production TLS material.

## Commands

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `identityhub.add-audience` | `AddAudience` | audience name |
| `identityhub.add-client` | `AddClient` | client id |

The typed IIdentityHubResourceDescriptor retains its command surface through DependsOn chaining.
Audience and client commands are registered on the default control plane and served by the existing
authenticated API endpoint, with POST/DELETE and JSON refusal observations. Owner must equal the
authenticated application issuer; transport and bootstrap-token verification are unchanged.

The command registry is written atomically to `registry.json` beside IdentitySigningKey under the
resource data path. Startup restores the registry and control-plane ownership before serving.
Token issuance reads the live combined registry of builder-declared and command-declared clients;
discovery and JWKS remain the same issuer surface. Audience removal is rejected while a client uses it.

Client credentialSource names a resource Secret mount, optionally prefixed with `mount:`. The
declaration stores the mount name, never credential bytes. Hosting reads and hashes the mounted
credential when initializing or updating the registry, so restart requires the mount again.
Clients must reference existing audiences; add the audience before the client. Conflicting resource
seeds or changed client declarations are rejected until the owning declaration is deleted.

## Concrete composition (T10 / O34)

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration belongs to the concrete `IdentityHubApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<IdentityHubApplicationContext, IHostService>)`. The factory deliberately receives the concrete context, unlike Web's AddService and Database's AddServer interface-context overloads, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.
