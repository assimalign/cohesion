# Assimalign.Cohesion.IdentityHub.Hosting Design

## Issuer runtime

Every built host registers one internal HTTP issuer after user services. The endpoint is resolved from the generated control plane, the ambient `https` resource endpoint, `--endpoint`, then `https://127.0.0.1:8443`. State is resolved from the `data` mount, `--data`, then the resource content root. Plain HTTP is permitted only on loopback in Development.

The issuer exposes public OpenID Provider metadata, JWKS, and token endpoints. It accepts exactly the implemented `openid` scope or an empty scope and returns `invalid_scope` for every other client-controlled value. Client authentication accepts exactly one of HTTP Basic or form credentials, constrains the requested `audience`/`resource` to the registration, and issues ES256 bearer JWTs. Confidential clients authenticate at device authorization and polling as well as for client credentials.

Device authorization and the built-in verification page are available only when the issuer itself binds to loopback in Development. High-entropy device codes, human-readable user codes, a ten-minute lifetime, pending polling, one-shot consumption, and an ID token when `openid` was requested remain supported there. The page has no client-selected subject: every local approval signs in the fixed `development-user` identity, checks a supplied browser origin when present, and emits restrictive cache, framing, referrer, and content-security headers. Discovery omits the device endpoint and grant outside that safe development mode. Production account login, consent, recovery, federation, and subject selection require a separately authenticated user-flow implementation.

## Keys and tokens

The data mount contains one persisted P-256 PKCS#8 signing key. Creation uses an atomic same-directory move and owner-only permissions on Unix. JWKS publishes only public `EC`/`P-256`/`ES256` coordinates; `kid` is the RFC 7638 SHA-256 thumbprint of canonical public members. Token creation uses IdentityModel's `JsonWebTokenWriter`. Signing is serialized because the ECDSA instance is shared.

HTTPS consumes a materialized `tls` resource mount containing the PEM leaf certificate, matching private key, and optional chain. This uses the existing `ResourceMount` carrier, including its in-process bytes and Windows protected-file behavior. When that mount is absent, an ephemeral self-signed certificate is allowed only on loopback in Development; every other HTTPS binding fails closed with an explicit configuration error. TLS material is always distinct from the issuer signing key. The current runtime context does not carry an endpoint-to-certificate-mount association, so `tls` is the Hosting-local convention until that shared contract is extended.

Issued access tokens contain `iss`, `sub`, `aud`, `iat`, `nbf`, `exp`, `jti`, `client_id`, and token-use metadata. The configured lifetime is capped at 24 hours.

## Resource control plane

The builder captures `ResourceRuntime.Current`, resolves a registered area control plane from the executable assembly, and calls `ResourceRuntime.HostBuilt` exactly once. Public `/healthz`, `/readyz`, and `/livez` routes coexist with `/cohesion/v1/healthz`, `/readyz`, `/livez`, `/endpoints`, `/stop`, and `/commands`.

Every `/cohesion/v1/*` request is authenticated when a gateway is ambient. The verifier parses the application trust JWK, verifies the ES256 signature explicitly, then validates issuer, gateway subject, resource audience, required claims, temporal bounds, token ID, and a maximum 24-hour lifetime. Missing or malformed credentials return 401 with `WWW-Authenticate: Bearer`; a valid token for another resource returns 403. The default command list is empty because declarative `AddAudience`/`AddClient` gateway commands belong to later item 31c.

## AOT and dependency boundary

Hosting privately composes Cohesion Web/HTTP/connection libraries and the shared IdentityModel JWT implementation. Routes use direct dispatch and `Utf8JsonWriter`; there is no reflection-based routing, serializer metadata discovery, or dynamic activation.
