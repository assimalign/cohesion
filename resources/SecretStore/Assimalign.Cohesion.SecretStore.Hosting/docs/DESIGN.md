# Assimalign.Cohesion.SecretStore.Hosting Design

## Design intent

The hosting module is the SecretStore runtime and protocol boundary. Public construction remains
`SecretStoreApplication.CreateBuilder(args)` through the area-root
`ISecretStoreApplicationBuilder`; the builder, context, host, persistence implementation,
certificate-authority manager, issuer store, and endpoint service remain internal.

The builder captures the current `ResourceRuntime.Current` context when it is created. Tests and
in-process callers therefore install a per-invocation context with
`ResourceRuntime.CreateScope(...)` before calling `CreateBuilder`. When a control-plane factory is
registered for the resource assembly, the builder creates it, publishes the built host through
`ResourceRuntime.HostBuilt`, and serves its health, observed-endpoint, stop, and command surface.
A disabled/plain executable has no registered control plane and still gets the SecretStore data
protocol.

## Composition and endpoint selection

`Local` is the developer-machine environment. `Development` is a deployable environment and requires HTTPS even on loopback, as does `Production`. An unset environment still resolves to `Production`.

Every build materializes caller-added `IHostService` factories once, in registration order, then
appends one `SecretsEndpointService`. The shared host starts in that order and stops in reverse,
so the data endpoint starts last and drains first. A builder may be built only once.

The `api` endpoint is selected in this order:

1. an already observed `api` endpoint from the registered control plane;
2. the ambient resource context's `api` endpoint;
3. `--endpoint <uri>` or `--endpoint=<uri>`;
4. `https://127.0.0.1:8443`.

Only absolute `http` and `https` endpoint URIs are accepted. Hosts must be `localhost` or a
bindable IP address. Plaintext HTTP is restricted to loopback in the `Local` environment;
HTTPS is otherwise required. An unauthenticated standalone store is loopback-only for either
scheme. A non-root path on the endpoint URI becomes a prefix for every route below.

The durable data directory is selected from the ambient `data` mount, then `--data`, then
`<content-root>/data`. The mount must expose a file-system path. Command-line values are snapshots
owned by the builder and malformed or missing values fail during build.

## HTTP protocol

The host implements these routes over a real HTTP/1 listener:

| Route | Methods | Behavior |
| --- | --- | --- |
| `/healthz`, `/readyz`, `/livez` | `GET`, `HEAD` | Compatibility health routes. Readiness additionally requires a started host and an enrolled CA. |
| `/cohesion/v1/healthz`, `/readyz`, `/livez` | `GET`, `HEAD` | Authenticated namespaced health reports. |
| `/cohesion/v1/endpoints` | `GET`, `HEAD` | Returns observed endpoint strings. |
| `/cohesion/v1/stop` | `POST` | Requests control-plane stop, or returns `404` without a registered control plane. |
| `/cohesion/v1/secrets?path=<path>` | `GET`, `HEAD` | Reads protected secret bytes. `trusted-issuers.json` exports the current issuer document. |
| `/cohesion/v1/certificates?name=<name>` | `GET`, `HEAD` | Returns the root (`ca/root`) or a durable leaf bundle (`certs/<one-segment-name>`). |
| `/cohesion/v1/commands` | `GET`, `HEAD`, `POST`, `DELETE` | Lists accepted kinds; mutates declared secrets/certificates; trust grants retain POST-only behavior. |
| `/cohesion/v1/certificates/enrollment-request` | `GET`, `HEAD` | Returns the pending CSR for `application` and `resource`. |
| `/cohesion/v1/certificates/enroll` | `POST` | An enrolled authority signs a matching intermediate-CA CSR. |
| `/cohesion/v1/certificates/enrollment` | `POST` | Completes a pending enrollment with the issued certificate and issuer chain. |

Unsupported methods return `405` with `Allow`; malformed input returns `400`; unknown namespaced
routes return `404`. Secret and certificate responses are marked `no-store, no-cache`. `HEAD`
follows `GET` validation and headers without writing a body.

`SecretStore.Client` interoperates directly with the secret, certificate, and command routes.
The command JSON contains `id`, `kind`, `owner`, `key`, and base64 `payload`. The implemented
`cohesion.trust.add` command upserts an ES256/P-256 public JWK by issuer. Replacing the ambient
application issuer is rejected, and replacing an issuer owned by another authenticated principal
returns `409`.

## Bootstrap authentication

Authentication is enabled when the ambient context names a gateway. At startup the host requires
an application name, resource name, and public application trust JWK, and durably installs that
application as a trusted issuer. Every `/cohesion/v1/*` request then requires a bearer JWT signed
by a trusted ES256/P-256 key. The token requires `iss`, `sub`, `aud`, `exp`, `nbf`, `iat`, and
`jti`, has a maximum 24-hour lifetime, and must name the current resource as an audience.
Secret, certificate, trust-command, lifecycle, and child-enrollment operations additionally
require the ambient application's issuer. Only the Platform intermediate-signing route accepts a
trusted peer issuer, and its request application must equal that authenticated issuer.

Missing, malformed, expired, untrusted, or incorrectly signed credentials return `401` and a
`WWW-Authenticate: Bearer` challenge. A valid token for the wrong audience returns `403`.
Trust commands must set `owner` to `<iss>@<sub>`; declared secret and certificate commands use `<iss>`. Standalone hosts (no gateway name) do
not require bearer authentication and therefore may bind only to loopback. Plaintext HTTP is
allowed only on loopback in `Local`, regardless of hosting mode. This is bootstrap
credential authentication, not a general user authorization or secret-policy engine.

## Protected persistence and certificate authority

Secrets, trusted issuers, CA state, pending enrollment private keys, and leaf bundles are written
through `Assimalign.Cohesion.Security.DataProtection`. The key ring is durable below the data
directory and is discriminated by application and resource identity. Builder-declared secrets
seed only absent paths; existing durable values win on restart. Trust upserts are persisted before
the live issuer map changes.

The file-system ACL on the `data` volume remains the confidentiality boundary for the key ring;
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

The private HTTPS endpoint certificate covers its bind host, resource name, generic-planner API
Service name, and both short and cluster-local Kubernetes Service DNS forms. The Platform/public
root still must be distributed to clients by the gateway trust bootstrap described below.

Platform enrollment is a safe explicit three-step protocol: the child creates and persists a CSR
and private key; an enrolled parent validates the requested application/resource subject and signs
an intermediate CA; the child verifies the returned certificate, chain, and optional pinned
Platform root before committing it and deleting pending state. A configured enrollment failure
never falls back to a new self-signed root.

## Explicit bootstrap gap

`PlatformEnrollmentEndpoint` selects pending-enrollment mode. Gateway-driven automatic enrollment
remains deferred at the client request/response seam; the store's CSR, signing, and completion
routes already exist. The gateway now supplies a provisional transport certificate and trusted
anchors, so a pending child can serve HTTPS before its own authority is enrolled. Readiness
remains unhealthy until enrollment completes. The listener chooses the contract certificate first
and its own CA second. Parameter-supplied certificates are resolved by the gateway; `public`
remains reserved for later ACME integration. Default issued leaves include localhost and both
loopback IP SANs so local clients can validate the same application certificate; explicitly supplied
SANs remain authoritative.

## Boundaries and AOT posture

The host references the SecretStore root plus cross-area hosting, HTTP, identity-token,
data-protection, and Web runtime infrastructure. Those cross-area runtime dependencies are private
implementation details. It does not reference SecretStore.ApplicationModel; the generated enabled
resource registers that package's control plane through Hosting.Resources. JSON used by the
endpoint is written explicitly, persistence uses fixed internal formats, and construction uses no
dynamic activation. The implementation remains trimming- and NativeAOT-oriented.

## Declarative commands (item 31c)

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `secretstore.add-secret` | `AddSecret` | secret path |
| `secretstore.issue-certificate` | `IssueCertificate` | certificate name |

Kinds use verb-noun kebab under the area prefix. The examples `rezolvr.record` and
`identityhub.audience` in developer-experience design section 7 are illustrative; item 27's design
rewrite should reflect the landed convention. Manifest commands remain bare JSON strings.
Typed verbs validate argument shape and use source-generated JSON metadata. Build validates the
advertised kind, canonical payload, deterministic id, and uniqueness of the target ownership key.
The default control plane handles id replay and owner isolation; each area handler also accepts
an identical reapplication with a different id. Conflicts return named Rejected details.

AddSecret declarations carry only a source reference. `parameter:<name>` resolves through the
gateway's existing parameter provider before delivery. `<resource>:<key>` uses the existing store
resolver and requires a declared dependency and an available source endpoint. `literal:<value>`
is rejected during declaration construction: literal secret material never enters the desired
model, deterministic id or manifest. Only the transient delivery envelope contains resolved bytes;
the protected repository stores the value and source together. An unresolved source is a named
Rejected result. Original source-only commands remain the gateway's declaration ledger.

IssueCertificate honors the supplied subject and SAN set. An existing certificate with different
identity is rejected until deleted; renewal preserves its identity. Private key and leaf storage
reuse the existing protected CA repository.

The control plane also accepts `cohesion.trust.add`, which the SDK manifest deliberately does not
advertise. It is the gateway-owned trust channel through IGatewayStoreClient, never a Build-declared
application command. Trust keeps owner `issuer@subject`, POST-only behavior, empty 204 success,
empty 409 conflict and existing 403 authorization refusals. New commands use owner `issuer`, accept
POST and DELETE, return 200 application/octet-stream on success, and JSON `{status,detail}` refusals.
Malformed command envelopes return 400 with that JSON refusal shape, including invalid base64 payloads.
The client accepts empty successful responses as Applied (Deleted for DELETE); legacy SendCommandAsync
continues to work. This owner split lets local gateway declarations authenticate end to end.

Restricted trust grants accept `{trustKey,allowedCommandKinds}` while unrestricted grants retain
the bare JWK payload. The protected trust document round-trips the optional string array; absent
or empty means every command kind is allowed. Enroll(platformStore) remains deferred after item 31t:
automatic Platform enrollment needs a gateway-owned mediator and Platform-audience signer.

## HTTPS endpoint certificate contract (31t)

The enabled resource's `api` listener consumes the shared Hosting.Resources endpoint certificate accessor. Endpoint metadata identifies an ordinary Secret mount (default `tls`), carrying one PEM leaf/private-key/chain document; existing hand-authored IdentityHub and LogSpace bundles retain the same format. Empty mounts are absent; malformed or multi-key bundles fail. TLS options are composed in Hosting from the returned leaf and chain, with no hosting-isolation exemptions or dependency changes. Plain application composition is unchanged. The mounted transport certificate wins before the store-owned CA fallback, so pending intermediate enrollment can expose its HTTPS listener independently of CA readiness.

## Optional telemetry (31b)

The registered resource constructor calls ResourceTelemetry.Configure using the invocation snapshot. With no gateway or telemetry endpoint, existing providers and hosted services are unchanged. When enabled, the shared Hosting.Telemetry sibling adds OTLP/HTTP JSON logging and a service registered before producers; reverse StopAsync drains producers before a flush bounded by five seconds and the host shutdown token. Logging remains composed only in Hosting. See libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md for ordering and protocol limits.
