# Assimalign.Cohesion.IdentityModel.Protocols

## Summary

The shared, transport-agnostic protocol abstractions of the Cohesion
IdentityModel family — the base every protocol contract branch (OpenID Connect,
SAML 2.0, and future protocols) derives from. It owns the cross-protocol
concepts that recur across authentication protocols so each branch models only
what is genuinely protocol-specific:

- **Open vocabularies**: `ProtocolRole`, `ProtocolBinding` (transport shapes),
  `ProtocolEndpointKind`.
- **Parties and metadata**: `ProtocolParty` references and the published-entity
  metadata base (`ProtocolMetadata` with role-scoped `ProtocolEndpoint` and
  `ProtocolKey` lists).
- **Message envelopes**: `ProtocolMessage` → `ProtocolRequest` /
  `ProtocolResponse`, with pinned correlation semantics, fail-closed
  `ProtocolResponseStatus`, and the shared logout request/response semantics.
- **Exchange and validation**: the two-leg `ProtocolExchange` description and
  the validation currency (`ProtocolValidationResult`,
  `ProtocolValidationDiagnostic`, `ProtocolValidationSeverity`, and the
  cross-protocol `ProtocolValidationCodes`).

## Status

Delivered by feature `[L01.01.12.03]` (#592). Consumed by the OpenID Connect
branch (`…Protocols.OpenIdConnect`) and, when it lands, the SAML branch
(`…Protocols.Saml`).

## Dependencies

- `Assimalign.Cohesion.IdentityModel` (the canonical model + the shared
  internal `ModelSnapshot` materialization helper). BCL otherwise; no
  `Microsoft.Extensions.*`, no transport or serializer dependencies.

## Scope

Descriptive, not executable: these are contract types over protocol *data*.
No HTTP, no XML runtime, no cryptography. See the family
[DESIGN.md](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) —
"Shared protocol abstraction decisions" — for the load-bearing design
rationale.
