# Assimalign.Cohesion.IdentityModel.Token.JsonWebToken — Design

## Design intent

The concrete JOSE/JWT document layer: it takes the neutral token base to the fidelity OpenID
Connect identity-token work needs — a typed JOSE header, compact-serialization parsing, the
registered/OIDC claim surface, and document-level validation — without becoming an OpenID
Connect flow engine. It derives from `IdentityToken` and pins its document format.

The load-bearing rationale — the crypto boundary (keyless hashing in, keyed verification out),
the JWT/OIDC validation split, the claim-name mirror, `Claims`-as-authoritative with computed
accessors, and the fail-closed parsing rules — is recorded in the family keystone,
[`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md).
This document records the project-boundary specifics.

## The crypto boundary (the line is key material)

The family defers *keyed* cryptography — signature/MAC creation and verification, key
management, encryption/decryption — to a future Security-layer package, because those require
key material and algorithm-suite dispatch. This package therefore:

- **Executes** the keyless, deterministic operations a JWT document must perform to check its
  own self-consistency: `alg=none` rejection (RFC 8725), and the `at_hash`/`c_hash` half-SHA-2
  base64url comparison (OIDC Core §3.1.3.6 / §3.3.2.11), whose digest size is read from the JWS
  `alg` name (`*256`→SHA-256, `*384`→SHA-384, `*512`→SHA-512; `none`/EdDSA/unknown have no
  defined pairing and yield a diagnostic, never a silent pass).
- **Exposes but never executes** signature verification: `SigningInput` (the ASCII
  `header.payload` using the encoded segments as received) and `Parts.Signature` are the seam a
  Security-layer verifier consumes. A `Validate` that returns `Succeeded` means "data + hash
  rules passed" — explicitly **not** "signature verified".

## The JWT / OpenID Connect validation split

The JWT package cannot reference the OpenID Connect protocol branch (branch independence,
enforced by architecture tests), and the two are complementary, not duplicative:

| Concern | Owner |
|---|---|
| JOSE `alg` presence / `none` / allowed-set; `b64`; `crit` | JWT package |
| `at_hash`/`c_hash` presence-plus-value (keyless hash) | JWT package |
| Required-claim presence (a caller-supplied set) | JWT package |
| Issuer / audience / temporal (neutral) | Token base (composed by both) |
| `nonce` match, `azp`-equals-client, `max_age`, additional-audience trust | OpenID Connect branch |
| Signature / MAC verification, key resolution | Security-layer seam (deferred) |

When the same physical JWT is also materialized as an `OpenIdConnectIdToken`, issuer/audience/
temporal are checked by both validators by design — the JWT check is the document-substrate
guarantee, the OIDC check is the Core §3.1.3.7 protocol-profile guarantee. This is complementary
layering; do not consolidate them, or standalone JWT validation breaks.

## Fidelity and fail-closed parsing

- **Duplicate members** are rejected (RFC 8725 §2.3) rather than silently last-wins-resolved,
  by explicit detection during the token-by-token walk.
- **NumericDate** (`exp`/`nbf`/`iat`/`auth_time`) is kept wire-shaped (integer seconds) in
  `Claims`; the base temporal members and the `AuthTime` accessor are *bounded* projections that
  degrade an out-of-range value (`exp: 1e300`) to `null` rather than throwing.
- **`aud`** accepts a single string or an array of strings (RFC 7519 §4.1.3), folded into both
  the neutral audience list and one claim per audience so the two surfaces agree.
- **Numbers** map integral→`Integer`, otherwise→`Double`; **objects/arrays** map recursively and
  are bounded by `IdentityClaimValue.MaxDepth`.
- The typed `JoseHeader` accessors are computed from the single authoritative `Parameters`
  record; the computed claim accessors read from the authoritative `Claims` — neither holds a
  copy that can drift.

## Compatibility matrix

| Capability | Status |
|---|---|
| Compact JWS parsing (header/payload/signature) | Implemented |
| JOSE header params (`alg`/`typ`/`cty`/`kid`/`crit`/`b64`/x5*/jku/jwk) | Implemented (typed + raw) |
| Registered + OIDC ID-token claim capture | Implemented |
| `alg=none` rejection (RFC 8725) | Implemented |
| Allowed-algorithm / required-claim checks | Implemented |
| `at_hash`/`c_hash` keyless value comparison | Implemented |
| Duplicate-member / malformed / over-deep rejection | Implemented |
| Signature / MAC verification | Seam only (deferred to Security layer) |
| Unencoded payload (`b64:false`, RFC 7797) | Rejected (unsupported) |
| JWE (encrypted tokens, RFC 7516) | Deferred |
| OIDC protocol rules (nonce/azp/`max_age`) | Out of scope (OpenID Connect branch) |

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>`. Parsing uses `System.Text.Json`'s reflection-free
`JsonDocument`/`Utf8JsonReader` — never `JsonSerializer` over unannotated graphs; hashing uses
`System.Security.Cryptography` one-shot `HashData`; base64url uses `System.Buffers.Text.Base64Url`
— all in-box for `net10.0`, no package references, no reflection. Representative NativeAOT
publish evidence for the family is the epic's final feature; this package is AOT-clean by
construction.

## Non-goals

Signature/MAC verification and key management (Security-layer seam), JWE, and OpenID Connect
protocol-flow validation are out of scope. The package models signed compact serialization
(JWS) only.
