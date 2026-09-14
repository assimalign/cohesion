# Assimalign.Cohesion.IdentityHub.Hosting Design

## Issuer runtime

Every built host registers one internal HTTP issuer after user services. The endpoint is resolved from the generated control plane, the ambient `https` resource endpoint, `--endpoint`, then `https://127.0.0.1:8443`. State is resolved from the `data` mount, `--data`, then the resource content root. Plain HTTP is permitted only on loopback in Development.

The issuer exposes public OpenID Provider metadata, JWKS, and token endpoints. It accepts exactly the implemented `openid` scope or an empty scope and returns `invalid_scope` for every other client-controlled value. Client authentication accepts exactly one of HTTP Basic or form credentials, constrains the requested `audience`/`resource` to the registration, and issues ES256 bearer JWTs. Confidential clients authenticate at device authorization and polling as well as for client credentials.

Device authorization and the built-in verification page are available only when the issuer itself binds to loopback in Development. High-entropy device codes, human-readable user codes, a ten-minute lifetime, pending polling, one-shot consumption, and an ID token when `openid` was requested remain supported there. The page has no client-selected subject: every local approval signs in the fixed `development-user` identity, checks a supplied browser origin when present, and emits restrictive cache, framing, referrer, and content-security headers. Discovery omits the device endpoint and grant outside that safe development mode. Production account login, consent, recovery, federation, and subject selection require a separately authenticated user-flow implementation.

## Keys and tokens

The data mount contains one persisted P-256 PKCS#8 signing key. Creation uses an atomic same-directory move and owner-only permissions on Unix. JWKS publishes only public `EC`/`P-256`/`ES256` coordinates; `kid` is the RFC 7638 SHA-256 thumbprint of canonical public members. Token creation uses IdentityModel's `JsonWebTokenWriter`. Signing is serialized because the ECDSA instance is shared.

HTTPS consumes a materialized `tls` resource mount containing the PEM leaf certificate, matching private key, and optional chain. This uses the existing `ResourceMount` carrier, including its in-process bytes and Windows protected-file behavior. When that mount is absent, an ephemeral self-signed certificate is allowed only on loopback in Development; every other HTTPS binding fails closed with an explicit configuration error. TLS material is always distinct from the issuer signing key. ResourceContext resolves generated endpoint-to-mount metadata, with `tls` as the compatibility fallback. The shared reader preserves the same single PEM document and certificate chain.

Issued access tokens contain `iss`, `sub`, `aud`, `iat`, `nbf`, `exp`, `jti`, `client_id`, and token-use metadata. The configured lifetime is capped at 24 hours.

## Resource control plane

The builder captures `ResourceRuntime.Current`, resolves a registered area control plane from the executable assembly, and calls `ResourceRuntime.HostBuilt` exactly once. Public `/healthz`, `/readyz`, and `/livez` routes coexist with `/cohesion/v1/healthz`, `/readyz`, `/livez`, `/endpoints`, `/stop`, and `/commands`.

Every `/cohesion/v1/*` request is authenticated when a gateway is ambient. The verifier parses the application trust JWK, verifies the ES256 signature explicitly, then validates issuer, gateway subject, resource audience, required claims, temporal bounds, token ID, and a maximum 24-hour lifetime. Missing or malformed credentials return 401 with `WWW-Authenticate: Bearer`; a valid token for another resource returns 403. The default control plane serves the declarative AddAudience and AddClient command kinds.

## AOT and dependency boundary

Hosting privately composes Cohesion Web/HTTP/connection libraries and the shared IdentityModel JWT implementation. Routes use direct dispatch and `Utf8JsonWriter`; there is no reflection-based routing, serializer metadata discovery, or dynamic activation.

## Declarative commands (item 31c)

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `identityhub.add-audience` | `AddAudience` | audience name |
| `identityhub.add-client` | `AddClient` | client id |

Kinds use verb-noun kebab under the area prefix. The examples `rezolvr.record` and
`identityhub.audience` in developer-experience design section 7 are illustrative; item 27's design
rewrite should reflect the landed convention. Manifest commands remain bare JSON strings.
Typed verbs validate argument shape and use source-generated JSON metadata. Build validates the
advertised kind, canonical payload, deterministic id, and uniqueness of the target ownership key.
The default control plane handles id replay and owner isolation; each area handler also accepts
an identical reapplication with a different id. Conflicts return named Rejected details.

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

## HTTPS endpoint certificate contract (31t)

The enabled resource's `https` listener consumes the shared Hosting.Resources endpoint certificate accessor. Endpoint metadata identifies an ordinary Secret mount (default `tls`), carrying one PEM leaf/private-key/chain document; existing hand-authored IdentityHub and LogSpace bundles retain the same format. Empty mounts are absent; malformed or multi-key bundles fail. TLS options are composed in Hosting from the returned leaf and chain, with no hosting-isolation exemptions or dependency changes. Plain application composition is unchanged. The existing loopback Development fallback and plaintext restriction remain; production errors continue to name tls.
