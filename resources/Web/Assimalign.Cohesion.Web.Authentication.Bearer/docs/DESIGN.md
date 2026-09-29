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

`JwtSignatureVerifier.CreateHmac/CreateRsa/CreateEcdsa` remain the public Bearer factories; the
adapter and all concrete verifiers stay internal. This preserves configured applications while
making the asymmetric primitive available below Web.

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
