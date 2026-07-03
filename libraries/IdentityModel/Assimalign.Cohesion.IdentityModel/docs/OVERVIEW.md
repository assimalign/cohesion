# Assimalign.Cohesion.IdentityModel

## Summary

The root contract package of the Cohesion IdentityModel family and the
dependency anchor for authentication across the platform. It owns:

- The **canonical identity domain model**: subjects, application identities,
  actors, credentials, claims and multi-value attributes, authentication
  sessions, authentication context, and authentication results.
- The **shared protocol abstractions**: protocol parties and roles, protocol
  metadata, request/response envelopes, validation results, logout semantics,
  and binding descriptors.
- The **first-class protocol contract branches** for OpenID Connect
  (`Protocols.OpenIdConnect`) and SAML 2.0 (`Protocols.Saml`).

Descendant packages (`…IdentityModel.Token`, `…Token.JsonWebToken`,
`…Token.Saml`) build concrete token-document behavior on these contracts;
none of them defines parallel foundational identity abstractions.

## Status

Epic `[L01.01.12]` (#105) is in delivery. Currently shipped:

- Family design, namespace map, ownership boundaries, and dependency rules
  (`docs/DESIGN.md`), enforced by architecture tests in
  `Assimalign.Cohesion.IdentityModel.Tests`.
- The canonical identity domain model (`[L01.01.12.02]`), replacing the
  legacy thin markers: `IIdentitySubject`/`IdentitySubject` with actor
  delegation chains, `SubjectIdentifier` + `SubjectIdentifierFormats`,
  `IdentityKind` (with an honest `Unknown` default), the typed claim layer
  (`IIdentityClaim`, `IdentityClaimValue` + `IdentityValueKind`,
  `IdentityClaimProvenance`, `IIdentityClaimCollection`,
  `IdentityAttribute`, `IdentityClaimTypes`), credentials
  (`IdentityCredential` + kind/state), and the authentication layer
  (`AuthenticationProtocol`, `AuthenticationContext`,
  `AuthenticationSession`, `AuthenticationResult`, `AuthenticationFailure`),
  plus the `IdentityModelException` area root and claim-collection
  `extension(…)` accessors.

- The shared protocol abstractions (`[L01.01.12.03]`) in the `Protocols`
  namespace: the open vocabularies (`ProtocolRole`, `ProtocolBinding`,
  `ProtocolEndpointKind`), party references, the published-entity metadata
  base (`ProtocolMetadata` + endpoints and keys with role scoping), the
  message envelope hierarchy (`ProtocolMessage`/`Request`/`Response` with
  pinned correlation semantics and fail-closed `ProtocolResponseStatus`),
  shared logout semantics, the two-leg `ProtocolExchange` description, and
  the validation-result contracts (`ProtocolValidationResult` /
  `ProtocolValidationDiagnostic`).

Remaining features land in dependency order: OIDC branch, SAML branch, then
token-package alignment and cross-protocol normalization. See the delivery
roadmap table in `docs/DESIGN.md`.

## Scope

This package is deliberately descriptive, not executable. It does NOT:

- Execute protocol flows, host endpoints, or perform HTTP/XML transport work
- Perform cryptographic signing, verification, or key management
- Serialize or deserialize wire formats (consumers bring source-generated
  serializers; token packages own their document parsing)
- Depend on anything outside the BCL — no `Microsoft.Extensions.*`, no
  transport or hosting packages

## Dependencies

None (BCL only). The rest of the family depends one-way on this package:

```
…IdentityModel ← …IdentityModel.Token ← { …Token.JsonWebToken, …Token.Saml }
```

## Test coverage

`Assimalign.Cohesion.IdentityModel.Tests` contains:

- `IdentityModelFamilyBoundaryTests` — dependency-direction guards: the root
  references no Cohesion assemblies, descendants reference only their parent
  chain, sibling token packages never reference each other, and no family
  assembly references `Microsoft.Extensions.*`.
- `IdentityModelNamespaceAlignmentTests` — every public type in every family
  assembly lives under its assembly-name namespace, keeping the documented
  namespace map honest.
- Domain-model suites — claim value shapes, snapshot/defensive-copy and
  depth-cap invariants, subject kind differentiation and actor chains,
  identifier equality semantics, credential and session lifecycle validity,
  result invariants, and `CanonicalModelFidelityTests` proving the same
  principal maps from OIDC-shaped and SAML-shaped data with provenance
  intact.

Protocol-contract and compliance-fixture suites are added by the features
that introduce those surfaces.
