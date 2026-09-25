# Assimalign.Cohesion.IdentityModel.Token.JsonWebToken

## Summary

The concrete JSON Web Token (JOSE/JWT) document layer of the Cohesion IdentityModel family. It
takes the neutral token base (`…Token`) to OIDC-grade JWT document fidelity:

- **`JoseHeader`** — a typed JOSE header (RFC 7515 §4.1: `alg`, `typ`, `cty`, `kid`, `crit`,
  `b64`, …) projecting a single authoritative `IdentityClaimValue` `Parameters` record.
- **`JsonWebToken`** — the token materialized onto the canonical model, with computed accessors
  for the registered and OpenID Connect ID-token claims (`nonce`, `azp`, `auth_time`,
  `at_hash`, `c_hash`, `sid`, `acr`, `amr`) read from the authoritative `Claims`.
- **Parsing** — `JsonWebToken.Parse` / `TryParse`: reflection-free compact-serialization
  parsing (base64url + `System.Text.Json` readers) that rejects the RFC 8725 §2.3
  duplicate-member ambiguity, bounds NumericDate conversion, and folds string-or-array `aud`.
- **Writing** — `JsonWebTokenWriter.CreateEs256`: compact JWS creation over the existing
  `JsonWebTokenDescriptor`, with `alg`/`kid` headers, registered-claim helpers, NIST P-256
  curve binding, and IEEE P1363 signatures produced by `ECDsa.SignData`.
- **Signature verification** — `JsonWebTokenSignatureVerifier.CreateEcdsa/CreateRsa`: reusable,
  key-bound ECDSA (`ES*`) and RSA (`RS*`/`PS*`) verification over the exact compact signing input.
- **Validation** — `JsonWebToken.Validate(JsonWebTokenValidationOptions)`: algorithm presence /
  `none` rejection (RFC 8725) / allowed-set membership, required-claim presence, `b64`/`crit`
  constraints, and the **keyless** `at_hash`/`c_hash` value comparison (OIDC Core §3.1.3.6 /
  §3.3.2.11), composing the neutral issuer/audience/temporal base rules.
- **Constants** — `JoseAlgorithms`, `JoseHeaderParameterNames`, `JsonWebTokenClaimTypes`,
  `JsonWebTokenValidationCodes`.

## Status

Document parsing and validation were delivered by feature `[L01.01.12.07]` (#608). Compact
ES256 writing and reusable asymmetric signature verification were delivered by design item 25b,
`[L01.01.12.16]` (#970), before the bootstrap-credential feature.

## Dependencies

- `Assimalign.Cohesion.IdentityModel.Token` (and transitively the root canonical model). BCL
  otherwise (`System.Text.Json`, `System.Security.Cryptography`, `System.Buffers.Text` — all
  in-box for `net10.0`, reflection-free, AOT-safe). No `Microsoft.Extensions.*`. It does **not**
  reference the OpenID Connect protocol branch.

## Scope

Compact JWT/JWS document fidelity, ES256 writing, reusable asymmetric signature primitives, and
document-level validation. `Validate` deliberately does **not** invoke a signature verifier;
callers verify `SigningInput` + `Parts.Signature` separately before trusting claims. The package
does not manage keys/trust, enforce OpenID Connect protocol rules (nonce/azp/`max_age` — the OIDC
branch's concern), or handle JWE. A successful `Validate` means "data and hash rules passed",
never "signature verified". See the family
[DESIGN.md](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) — "Token normalization
decisions" — and this project's [DESIGN.md](DESIGN.md) for the boundary and compatibility
matrix.
