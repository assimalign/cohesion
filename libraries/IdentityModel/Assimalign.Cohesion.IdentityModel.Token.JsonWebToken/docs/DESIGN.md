# Assimalign.Cohesion.IdentityModel.Token.JsonWebToken — Design

## Design intent

The concrete JOSE/JWT layer: it takes the neutral token base to the fidelity OpenID Connect
identity-token and Cohesion bootstrap-credential work needs — typed JOSE headers, compact JWS
parsing and ES256 writing, registered/OIDC claims, document validation, and reusable asymmetric
signature verification — without becoming an OpenID Connect flow engine or a key-management
system. It derives from `IdentityToken` and pins its document format.

The load-bearing rationale — the JWT/OIDC validation split, claim-name mirror,
`Claims`-as-authoritative materialized model, and fail-closed parsing rules — is recorded in the
family keystone,
[`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md).
This document records the project-boundary specifics.

## The cryptographic boundary is format execution versus key management

Design item 25b (`#970`) places the compact-JWS primitives here so headless resources can mint
or verify Cohesion bootstrap credentials without depending on the Web area. This supersedes the
older plan in `#830` that assigned all keyed token cryptography to a future Security package.
The boundary is now:

- **This package executes format-specific BCL primitives.** `JsonWebTokenWriter.CreateEs256`
  returns an `IJsonWebTokenWriter` backed by an internal implementation that writes compact JWS
  and calls `ECDsa.SignData` with SHA-256 and the IEEE P1363 fixed-field `r||s` format.
  `JsonWebTokenSignatureVerifier.CreateEcdsa/CreateRsa` return
  `IJsonWebTokenSignatureVerifier` instances backed by internal ECDSA/RSA implementations.
- **Callers own keys and trust policy.** Writers and verifiers borrow key instances; they never
  generate, persist, rotate, resolve, dispose, or publish keys. JWK/JWKS parsing and retrieval,
  trusted-issuer storage, and revocation remain outside this package.
- **`JsonWebToken.Validate` remains document-only.** It composes issuer, audience, temporal,
  algorithm, critical-header, and hash rules, but never invokes a signature verifier. A caller
  verifies the exact received `header.payload` and decoded signature before trusting claims.
  A successful `Validate` still means “data and hash rules passed,” not “signature verified.”

HMAC verification remains in `Web.Authentication.Bearer`; item 25b moved only the reusable
asymmetric implementations. The broader Security work in `#830` can still own JWE, SAML
XML-DSig/XML encryption, and key-resolution suites, but not these moved RSA/ECDSA primitives.

## Compact writing

The writer is interface-first: the public `IJsonWebTokenWriter` contract and static
`JsonWebTokenWriter` factory expose an internal ES256 implementation. The factory requires a
NIST P-256 ECDSA key and a non-empty `kid`; the writer owns both `alg=ES256` and `kid`, rejects those
parameters if the descriptor also supplies them, and never mutates the descriptor.

Payloads use the existing `JsonWebTokenDescriptor` model rather than an object-valued or
serializer-specific claim bag:

- `Issuer`, `Subject`, `Audiences`, `ExpiresAt`, `NotBefore`, `IssuedAt`, and `Id` project to
  `iss`, `sub`, `aud`, `exp`, `nbf`, `iat`, and `jti`.
- NumericDate helpers are emitted as integer Unix seconds; one audience is a string and multiple
  audiences are an array, matching RFC 7519.
- Remaining `IIdentityClaim` values are written through the closed `IdentityClaimValue` kind
  switch. Repeated non-singleton claim types become one JSON array, because duplicate JSON object
  members are forbidden. A registered claim supplied through both a typed helper and `Claims` is
  rejected instead of silently choosing precedence.
- Header and payload JSON are written directly with `Utf8JsonWriter`; there is no runtime type
  discovery or reflection-based serialization. The unencoded-payload (`b64:false`) variant is
  rejected.

JSON property order is deterministic for the typed registered members and preserves input order
for remaining descriptor members, but it is not a canonicalization contract. Verification always
uses the exact compact segments received; claim `Canonicalize` is name normalization and is
unrelated to JWS signing.

## Signature verification

`IJsonWebTokenSignatureVerifier` preserves the existing raw-byte seam:
`CanVerify(alg, kid)` plus `Verify(alg, signingInput, decodedSignature)`. Built-ins use:

- **RSA** — `RSA.VerifyData`, supporting `RS256/384/512` with PKCS#1 v1.5 and
  `PS256/384/512` with PSS.
- **ECDSA** — `ECDsa.VerifyData` with
  `DSASignatureFormat.IeeeP1363FixedFieldConcatenation`, because JWS carries raw `r||s`, never
  DER. Algorithm and named-curve OID are bound (`ES256`↔P-256, `ES384`↔P-384,
  `ES512`↔P-521), and P1363 lengths are enforced (64/96/132 octets), so a token cannot relabel a
  P-256 signature as `ES384` or substitute a same-size non-JOSE curve.

An optional configured `kid` compares ordinally; a null configured value is a wildcard. Unknown
algorithms, wrong key families, mismatched key sizes, invalid signatures, and BCL cryptographic
verification failures return `false`. `Web.Authentication.Bearer` retains its public
`IJwtSignatureVerifier` API and adapts its RSA/ECDSA factories to this lower seam.

## The JWT / OpenID Connect validation split

The JWT package cannot reference the OpenID Connect protocol branch (branch independence,
enforced by architecture tests), and the two are complementary:

| Concern | Owner |
|---|---|
| JOSE `alg` presence / `none` / allowed-set; `b64`; `crit` | JWT package |
| `at_hash`/`c_hash` presence-plus-value (keyless hash) | JWT package |
| Required-claim presence (a caller-supplied set) | JWT package |
| Issuer / audience / temporal (neutral) | Token base (composed by both) |
| RSA/ECDSA compact-JWS signing and verification primitives | JWT package |
| Key generation, persistence, trust resolution, revocation, JWKS retrieval | Calling service / future key-management packages |
| `nonce` match, `azp`-equals-client, `max_age`, additional-audience trust | OpenID Connect branch |

When one physical JWT is also materialized as an `OpenIdConnectIdToken`, issuer/audience/temporal
checks run in both validators by design — document substrate versus protocol profile.

## Fidelity and fail-closed parsing

- Duplicate JSON members are rejected (RFC 8725 §2.3), never last-wins-resolved.
- NumericDate values stay wire-shaped in `Claims`; bounded typed projections degrade an
  out-of-range value to null rather than throwing.
- `aud` accepts one string or an array of strings and folds both into the audience list and
  canonical claims.
- Numbers map integral→`Integer`, otherwise→`Double`; objects/arrays are recursive and bounded by
  `IdentityClaimValue.MaxDepth`.
- Typed header and claim accessors project from their authoritative materialized records.

## Compatibility matrix

| Capability | Status |
|---|---|
| Compact JWS parsing (header/payload/signature) | Implemented |
| Compact ES256 writing (`alg`/`kid`, descriptor claims) | Implemented |
| RSA (`RS*`/`PS*`) and ECDSA (`ES*`) signature verification | Implemented |
| JOSE header params and registered/OIDC claims | Implemented |
| Algorithm / required-claim / `at_hash` / `c_hash` validation | Implemented |
| Duplicate-member / malformed / over-deep rejection | Implemented |
| HMAC writing or lower-layer HMAC verification | Out of scope for item 25b |
| Unencoded payload (`b64:false`, RFC 7797) | Rejected (unsupported) |
| JWE (encrypted tokens, RFC 7516) | Deferred |
| OIDC protocol rules (nonce/azp/`max_age`) | OpenID Connect branch |

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>`. Parsing and writing use reflection-free
`JsonDocument`/`Utf8JsonWriter`; hashing and signatures use BCL one-shot
`HashData`/`SignData`/`VerifyData`; base64url uses `System.Buffers.Text.Base64Url`. There are no
package references, reflection, runtime code generation, or serializer metadata graphs.

## Non-goals

Key generation/storage/rotation, JWK/JWKS retrieval, trust-grant policy, HMAC writing, JWE, SAML
cryptography, and OpenID Connect protocol-flow validation are out of scope. This package owns the
compact JWT/JWS document and its reusable asymmetric format primitives, not the surrounding trust
system.
