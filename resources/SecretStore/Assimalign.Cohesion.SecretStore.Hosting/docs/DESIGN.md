# Assimalign.Cohesion.SecretStore.Hosting Design

## Design intent

The hosting module is the SecretStore runtime and protocol boundary. `SecretStoreApplication.CreateBuilder(args)` returns the public concrete `SecretStoreApplicationBuilder`; its `Build()` returns the public `SecretStoreApplication : Host<SecretStoreApplicationContext>`. The public `SecretStoreApplicationContext` implements `ISecretStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal. Persistence, the certificate-authority manager, and endpoint service remain internal.

The area's resource integration is `Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel` (`builder.Services.AddResource(...)`), which references this module; this module references no application-model library.

## Composition and endpoint selection

`Local` is the developer-machine environment. `Development` is a deployable environment and requires HTTPS even on loopback, as does `Production`. An unset environment still resolves to `Production`.

Every build materializes caller-added `IHostService` factories once, in registration order, then
appends one `SecretsEndpointService`. The shared host starts in that order and stops in reverse,
so the data endpoint starts last and drains first. A builder may be built only once.

The `api` endpoint is selected in this order:

1. `--endpoint <uri>` or `--endpoint=<uri>`;
2. `https://127.0.0.1:8443`.

Only absolute `http` and `https` endpoint URIs are accepted. Hosts must be `localhost` or a
bindable IP address. Plaintext HTTP is restricted to loopback in the `Local` environment;
HTTPS is otherwise required. An unauthenticated standalone store is loopback-only for either
scheme. A non-root path on the endpoint URI becomes a prefix for every route below.

The durable data directory is selected from `--data`, then
`<content-root>/data`. Command-line values are snapshots
owned by the builder and malformed or missing values fail during build.

## HTTP protocol

The host implements these routes over a real HTTP/1 listener:

| Route | Methods | Behavior |
| --- | --- | --- |
| `/healthz`, `/readyz`, `/livez` | `GET`, `HEAD` | Compatibility health routes. Readiness additionally requires a started host and an enrolled CA. |
| `/cohesion/v1/healthz`, `/readyz`, `/livez` | `GET`, `HEAD` | Namespaced health reports. |
| `/cohesion/v1/endpoints` | `GET`, `HEAD` | Returns the `api` endpoint string. |
| `/cohesion/v1/secrets?path=<path>` | `GET`, `HEAD` | Reads protected secret bytes. |
| `/cohesion/v1/certificates?name=<name>` | `GET`, `HEAD` | Returns the root (`ca/root`) or a durable leaf bundle (`certs/<one-segment-name>`). |
| `/cohesion/v1/certificates/enroll` | `POST` | An enrolled authority signs a matching intermediate-CA CSR. |
| `/cohesion/v1/certificates/enrollment` | `POST` | Completes a pending enrollment with the issued certificate and issuer chain. |

Unsupported methods return `405` with `Allow`; malformed input returns `400`; unknown namespaced
routes return `404`. Secret and certificate responses are marked `no-store, no-cache`. `HEAD`
follows `GET` validation and headers without writing a body.

`SecretStore.Client` interoperates directly with the secret and certificate routes.

## Access

Hosts do
not require bearer authentication and therefore may bind only to loopback. Each
`/cohesion/v1` or `/cohesion/v1/*` request additionally requires the transport connection's remote
endpoint to be `127.0.0.1`, `::1`, or IPv4-mapped `127.0.0.1`. Every other peer, including an absent
or non-IP endpoint, receives `403 Forbidden` before any route executes. The explicit `403` makes
the development-only access boundary clear. Forwarded headers never establish loopback identity.
Bare `/healthz`, `/readyz`, and `/livez` remain open for orchestrator and load-balancer probes;
their namespaced forms are restricted. The endpoint URI's path prefix applies to these rules.

This is a behavioral breaking change from 10.0.0-preview.1 (#1431, owner decision 46): the
standalone listener was already loopback-bound, but request authorization accepted any transport
peer reaching it. Plaintext HTTP is allowed only on loopback in `Local`.

## Protected persistence and certificate authority

Secrets, CA state, pending enrollment private keys, and leaf bundles are written
through `Assimalign.Cohesion.Security.DataProtection`. The key ring is durable below the data
directory. Builder-declared secrets
seed only absent paths; existing durable values win on restart.

The file-system ACL on the `data` directory remains the confidentiality boundary for the key ring;
this item does not add an external KMS or hardware-backed wrapping key. Files and directories are
restricted to their owner on POSIX. Keys rotate lazily on a 90-day lifetime. Until durable records
gain a rewrap migration, the store retains a long unprotect grace period so ordinary key rotation
cannot make an existing protected record unreadable.

CA initialization precedence is durable authority state, explicitly supplied certificate/private
key material, configured Platform enrollment, then standalone self-seeding. A self-seeded root is
an ECDSA P-256 CA. Leaf requests are issued on first resolution and persisted as PEM containing the
leaf private key and full issuer chain. Named leaves renew lazily on resolution within seven days
of expiration. A transport leaf is checked and renewed when the HTTPS listener starts; until the
shared TLS listener supports asynchronous per-handshake certificate selection, a continuously
running store must restart before that leaf expires. `ca/root` returns the terminal root.
`certs/public` is deliberately
`501 Not Implemented`: ACME/public-CA issuance is outside the current runtime.

The private HTTPS endpoint certificate covers its bind host, `secret-store`, and `secret-store-api`.
The Platform/public root still must be distributed to clients.

Platform enrollment is a safe explicit three-step protocol: the child creates and persists a CSR
and private key; an enrolled parent validates the requested application/resource subject and signs
an intermediate CA; the child verifies the returned certificate, chain, and optional pinned
Platform root before committing it and deleting pending state. A configured enrollment failure
never falls back to a new self-signed root.

## Explicit bootstrap gap

`PlatformEnrollmentEndpoint` selects pending-enrollment mode, which needs application and resource
names the host does not supply, so a host configured with it fails to start. `public`
remains reserved for later ACME integration. Default issued leaves include localhost and both
loopback IP SANs so local clients can validate the same application certificate.

## Boundaries and AOT posture

The host references the SecretStore root plus cross-area dependency-injection, hosting, HTTP,
data-protection, and Web runtime infrastructure. Those cross-area runtime dependencies are private
implementation details. JSON used by the
endpoint is written explicitly, persistence uses fixed internal formats, and construction uses no
dynamic activation. The implementation remains trimming- and NativeAOT-oriented.

## Concrete composition (T10 / O34)

Background-work registration belongs to the concrete `SecretStoreApplicationBuilder`: `AddService(IHostService)` and `AddService(Func<SecretStoreApplicationContext, IHostService>)`. The factory receives the same concrete context as Web's and Database's AddService, so hosting consumers can use environment, state, and hosted-service members beyond the small root contract. Database's root-level AddServer keeps the interface context. Factories run once per build against the same context retained by the application; the hosted-service snapshot is installed after factory evaluation. Services start in registration order and stop in reverse. No area-owned service abstraction is introduced.

The base host owns the already-cancelled run semantic: one complete start and graceful stop
with fresh lifecycle tokens, normal run-observer notifications, and a final Stopped state.
The concrete application and IHost route share it. Startup failures still roll back and propagate.

## Dependency injection composition

`SecretStoreApplicationBuilder.Services` is the application's one composition registry, created with `EnableDynamicCode = false`, `ValidateOnBuild = true`, and `ValidateScopes = true`. The module registers only factories and instances, so the provider stays reflection-free. Every lifecycle service is an `IHostService` registration: `AddService(IHostService)` registers the caller's instance, `AddService(Func<SecretStoreApplicationContext, IHostService>)` registers a factory closed over the context, and a direct `Services.AddSingleton<IHostService>(...)` joins the lifecycle the same way. Order is registration order: `SecretsEndpointService` registers in `Build`, so it starts last and stops first.

`Build` closes registration (the container becomes read-only, and a second `Build` throws), creates the provider once, and resolves `IEnumerable<IHostService>` once into the snapshot the host reuses for every start, stop, and rollback pass. The application owns the provider: disposal stops the host, then disposes the provider, which disposes every factory-created service in reverse creation order while instance registrations stay with their callers. A failed `Build` disposes the provider before rethrowing. The concrete context exposes the provider as `ServiceProvider` to hosting-layer factories; the area root and its features never see it.

Each `AddSecret` declaration and the `AddCertificateAuthority` options snapshot are registrations. The data-protection provider, `SecretStoreRepository`, and `CertificateAuthorityManager` are singletons the endpoint factory resolves, so the provider owns them and disposes them after the endpoint. `SecretsEndpointService` no longer disposes the certificate authority it is given, and `SecretStoreApplication` no longer holds the endpoint to dispose it. Both root verbs are explicit `ISecretStoreApplicationBuilder` shims over public concrete counterparts that return the builder for chaining.


## Validated declaration values (#1482)

The certificate authority keeps its leaf-name and subject-alternative-name predicates as private
members. The client owns the certificate reference predicate compiled here for
certificate reads; that is a source dependency, not an assembly reference. The runtime never
constructs a certificate merely to validate.

Certificate leaf names are nonblank segments, excluding `.`, `..`, slash
and backslash. SANs use IP parsing or the same default IDN conversion and ASCII representation
as `SubjectAlternativeNameBuilder.AddDnsName`, including supported wildcards. Certificate references
are exact `ca/root` or `certs/<leaf>` with a valid leaf segment. Original spelling is preserved.

`AddSecret` takes the root's generated `SecretStoreSeedPath`. Seed paths retain their distinct
rule: nonblank except exact `trusted-issuers.json`; `certs/` remains permitted. The builder rejects
`default`, and the repository validates raw seed dictionary keys with that same `IsValid`.

Compared with 10.0.0-preview.1, the public builder's seed parameter changes from `string` to
`SecretStoreSeedPath` (binary and interface-implementation source break; string calls still convert).
Whitespace-only leaf
segments are refused consistently. Invalid declarations and client references fail at composition
or before sending HTTP instead of failing at delivery. Valid wire payloads and storage keys retain
their spelling and representation. The declaration and client package designs list their signature
changes. Existing seed refusals are compatibility coverage, not a newly discovered defect.
