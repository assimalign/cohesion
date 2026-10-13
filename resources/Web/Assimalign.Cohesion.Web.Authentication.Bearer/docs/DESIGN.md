# Assimalign.Cohesion.Web.Authentication.Bearer &mdash; Design

## Design intent

The JWT bearer authentication scheme: read an `Authorization: Bearer`
credential, validate the token, and materialize a `ClaimsPrincipal`. It is
the `IAuthenticationHandler` implementation of the scheme model in
`Assimalign.Cohesion.Web.Authentication`, and it is stateless &mdash; every
request re-validates the caller-supplied token, so the handler is *not* an
`IAuthenticationSignInHandler` (there is no session to establish).

## Why it consumes IdentityModel rather than owning JWT crypto

The document-level validation &mdash; issuer, audience, lifetime,
algorithm allow-list, `none`-rejection, `crit`/`b64` header rules &mdash;
is delegated to `Assimalign.Cohesion.IdentityModel.Token.JsonWebToken`'s
`JsonWebToken.Parse` + `Validate(JsonWebTokenValidationOptions)` (the #610
contracts). The bearer handler does not re-implement any of it. This keeps
one JWT/JOSE implementation in the repo and lets the handler stay a thin
policy layer.

`JsonWebToken.Validate` deliberately remains document-only and does *not* check the signature.
Design item 25b (`#970`) moved the reusable RSA/ECDSA verifier behind that raw
`SigningInput`/`Parts` seam into IdentityModel's JWT package so headless resources do not acquire
a Web dependency. Bearer keeps the orchestration and its public compatibility seam.

## The signature-verification seam

`IJwtSignatureVerifier` is Bearer's stable keyed seam: `CanVerify(alg, kid)` +
`Verify(alg, signingInput, signature)`. Its factories provide:

- **HMAC** (`HS256/384/512`) &mdash; `HMACSHA*.HashData`, compared in fixed
  time via `CryptographicOperations.FixedTimeEquals`.
- **RSA** (`RS*` PKCS#1 and `PS*` PSS) &mdash; a thin adapter over
  `JsonWebTokenSignatureVerifier.CreateRsa` in IdentityModel.
- **ECDSA** (`ES256/384/512`) &mdash; a thin adapter over
  `JsonWebTokenSignatureVerifier.CreateEcdsa`, including named-curve and P1363-length binding.

`HmacJwtSignatureVerifier.Create`, `RsaJwtSignatureVerifier.Create` and
`EcdsaJwtSignatureVerifier.Create` return their public sealed implementations. The RSA and
ECDSA adapters are separate types; each delegates to the corresponding IdentityModel verifier
and borrows the caller's key. Their constructors are private.

**Algorithm-confusion defense is structural.** Because a verifier is bound
to concrete key material, its accepted algorithms are bounded by its key
type: an RSA verifier never claims to verify `HS256`. So the classic attack
&mdash; take an `RS256` token, flip `alg` to `HS256`, and sign with the RSA
*public* key as an HMAC secret &mdash; finds no willing verifier when the
scheme is configured with an RSA key. `AllowedAlgorithms` can narrow the
set further, and `none` is always rejected.

## Validation order

1. Parse the `Authorization` header; a missing or non-`Bearer` header is
   `NoResult` (not this scheme's credential), not a failure.
2. `JsonWebToken.TryParse` &mdash; malformed &rarr; `Fail`.
3. **Signature** &mdash; decode the base64url signature, verify over the
   ASCII `header.payload` signing input against each configured key until
   one accepts. No acceptance &rarr; `Fail`. (`none` and missing compact
   form &rarr; `Fail`.)
4. **Document rules** via `JsonWebToken.Validate` (temporal + algorithm +
   header constraints), with an injectable `TimeProvider` and configurable
   `ClockSkew`.
5. **Issuer / audience** &mdash; validated here (not through the single-value
   document options) so multiple valid issuers/audiences are accepted
   any-of.
6. Map claims onto a `ClaimsPrincipal`.

Signature is checked before the claims are trusted; issuer/audience after
the token is proven authentic and in-window.

## Fail-closed defaults (#1046)

Issuer and audience validation are **on by default** (`ValidateIssuer` and
`ValidateAudience` default to `true`). A scheme with validation on and an
empty `ValidIssuers` or `ValidAudiences` is a configuration error:

- `AddJwtBearer` validates the options when the scheme is registered, so the
  application fails at startup where the misconfiguration is written.
- `JwtBearerHandler.Create` runs the same check, for composition
  roots that build handlers themselves.
- The handler treats an empty list with validation on as matching nothing,
  which covers options mutated after registration.

Until 2026-09 an empty list meant "skip this check", so an application that
configured only signing keys accepted a token minted for any audience by any
issuer holding a trusted key (RFC 8725 §3.9). The opt-outs are explicit
properties rather than empty collections so the choice is visible in the
options. Turning issuer validation off is reasonable only when a key is shared
with exactly one issuer; turning audience validation off is rarely right for a
service that accepts tokens from a shared authority.

The algorithm set needs no separate default: with `AllowedAlgorithms` empty,
the accepted algorithms are exactly those the configured keys' verifiers
accept, and `none` is always rejected (see the algorithm-confusion paragraph
above).

## JWT &rarr; ClaimsPrincipal mapping

`JwtClaimsPrincipalMapper` is the single place IdentityModel's claim model
meets `System.Security.Claims`. It projects each `IIdentityClaim` to a
`Claim(type, value, valueType, issuer)`, expanding array-valued claims (a
`roles` array) into one claim per element so `ClaimsPrincipal.IsInRole` and
multi-value reads behave normally. The identity's authentication type is the
scheme name; `NameClaimType`/`RoleClaimType` are configurable (default
`name`/`roles`, RFC 9068). The projection is reflection-free.

## Challenges (RFC 6750)

`ChallengeAsync` emits `401` with `WWW-Authenticate: Bearer` &mdash; adding
`realm` when configured and, after a failed authenticate, `error="invalid_token"`
with a sanitized `error_description`. `ForbidAsync` emits `403` with
`Bearer error="insufficient_scope"`. Header values are sanitized of quotes
and CR/LF so they cannot break the quoted-string grammar. The bearer scheme
never redirects, so it needs no endpoint-metadata check.

Both paths append their challenge, preserving challenges already added by other schemes
(RFC 9110 §11.6.1, #1450). This is a behavior change from 10.0.0-preview.1, which
replaced earlier challenges: multi-scheme policies now advertise every alternative on
401 and preserve other challenges on 403. Authentication decisions and public APIs
are unchanged; this corrects challenge discovery, not an authentication bypass.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited. No reflection: JSON
parsing and asymmetric signature verification live in IdentityModel's reflection-free JWT
implementation, Web-local HMAC verification uses BCL one-shot APIs, and base64url is
`System.Buffers.Text.Base64Url`.

## Non-goals

- **Token acquisition / OAuth2 / OIDC client flows.** This handler only
  *validates* a presented token; obtaining one (authorization code, client
  credentials, refresh) is a follow-up behind the IdentityModel protocol
  packages.
- **JWKS / metadata discovery.** Keys are supplied directly as
  `IJwtSignatureVerifier`s; fetching and caching a signer's JWKS endpoint is
  a later addition on the same seam.
- **Token encryption (JWE).** Only signed (JWS) compact tokens are handled.

## Diagnostics

The internal `JwtBearerEventSource` provider is named
`Assimalign.Cohesion.Web.Authentication.Bearer`. It references
`Web.Server` for the request-id contract used by request correlation; the authentication dispatcher owns no source.

| Id | Event | Level | Payload |
| --- | --- | --- | --- |
| 1 | TokenRejected | Verbose | scheme, reason, algorithm (32 characters), keyId (128 characters), validationCode, requestId |

Reasons are EmptyCredential, Malformed, NotCompactJws, UnsecuredAlgorithm,
SignatureEncoding, NoMatchingKey, SignatureInvalid, ValidationFailed, IssuerRejected
and AudienceRejected. NoMatchingKey means no configured verifier accepts the
algorithm/key id; SignatureInvalid means an eligible verifier rejected the signature.
The authentication result and challenge text retain their existing semantics.

The lazily published PollingCounter `rejected-tokens` counts once per request and
scheme, including when no listener is attached. The per-scheme handler/result cache
prevents Authorization re-authentication and a subsequent challenge from counting again.
Missing/non-Bearer credentials and successful authentication produce neither event nor count.

All allocating payload work and request-id reads occur inside the Verbose enable check.
The request id comes from `IWebRequestIdFeature`, never an ambient Activity. Tokens,
Authorization headers, claim values and validation diagnostic messages never enter payloads.
The scope table's issuer field is deliberately omitted: JWT issuer is a claim and conflicts
with #1403's explicit no-claim-value acceptance criterion. Attacker-selected algorithm and
key id are untrusted text, truncated only when the event is enabled.

This adds diagnostics without changing public APIs or authentication decisions relative
to 10.0.0-preview.1. The separate #1450 challenge behavior change is described above.

## Public implementations and preview.1 compatibility (#1465)

Owner decision 59 promotes `JwtBearerHandler` and the three signature verifiers to public sealed
types with private constructors. Each creation method returns its own type and preserves its
parameters and validation; the handler and verifier interfaces remain the extension contracts.

10.0.0-preview.1 shipped the static `JwtBearerAuthentication` and `JwtSignatureVerifier`
classes. Their removal and replacement are source- and binary-breaking, accepted during
previews on decision 15's terms. Migrate and rebuild:

| Preview.1 call | Replacement |
| --- | --- |
| `JwtBearerAuthentication.CreateHandler(options)` | `JwtBearerHandler.Create(options)` |
| `JwtSignatureVerifier.CreateHmac(key, keyId)` | `HmacJwtSignatureVerifier.Create(key, keyId)` |
| `JwtSignatureVerifier.CreateRsa(publicKey, keyId)` | `RsaJwtSignatureVerifier.Create(publicKey, keyId)` |
| `JwtSignatureVerifier.CreateEcdsa(publicKey, keyId)` | `EcdsaJwtSignatureVerifier.Create(publicKey, keyId)` |

Authentication and cryptographic verification behavior are unchanged by this conversion.
