# Assimalign.Cohesion.IdentityModel.Token

## Summary

The protocol-neutral token/assertion normalization layer of the Cohesion
IdentityModel family — the layer between the root canonical model and the
concrete JWT (`…Token.JsonWebToken`) and SAML (`…Token.Saml`) token packages. A
materialized token exposes one normalized surface regardless of wire format:

- **`IIdentityToken`** / **`IdentityToken`**: the normalized token contract and
  its immutable base, expressed in the root canonical types —
  `SubjectIdentifier? Subject`, `IIdentityClaimCollection Claims`,
  `AuthenticationContext? AuthenticationContext`, `AuthenticationProtocol
  Protocol`, and `IReadOnlyDictionary<string, IdentityClaimValue> Properties` —
  plus issuer, audiences, declared type, the primary temporal window, and the
  raw payload.
- **`IdentityTokenDescriptor`**: the mutable descriptor a concrete token package
  fills and the base snapshots.
- **`IdentityTokenKind`**: the document format (`Unknown`/`JsonWebToken`/`Saml`),
  distinct from the protocol that produced the token.
- **Validation**: `IdentityToken.Validate(IdentityTokenValidationOptions)` — the
  one genuinely protocol-neutral behavior (issuer/audience/temporal) — returning
  a branch-local `TokenValidationResult` of `TokenValidationDiagnostic`s.
- **Extensions**: `HasAudience`, `IsActive`, `IsExpired` as `extension(...)`
  members; claim lookup uses the root `Contains`/`TryGet`/`GetAll`/`GetValues`.

## Status

Evolved by feature `[L01.01.12.06]` (#604) from a self-contained model into the
root-aligned normalization layer.

## Dependencies

- `Assimalign.Cohesion.IdentityModel` (the root canonical model) only. BCL
  otherwise; no `Microsoft.Extensions.*`. It does **not** reference the protocol
  branch (`…Protocols`) — the two branches are independent.

## Scope

Descriptive normalization, not execution. It does NOT parse compact JWTs or
assertion XML, verify signatures, or serialize tokens — those are the concrete
JWT/SAML token packages' concern. See the family
[DESIGN.md](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) — "Token
normalization decisions" — for the design rationale.
