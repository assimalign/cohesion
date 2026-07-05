# Assimalign.Cohesion.IdentityModel

## Summary

The root contract package of the Cohesion IdentityModel family and the
dependency anchor for authentication across the platform. It owns the
**canonical identity domain model**: subjects, application identities, actors,
credentials, claims and multi-value attributes, authentication sessions,
authentication context, and authentication results — plus the
`IdentityModelException` area root and the shared internal
descriptor-materialization helper the family builds on.

The protocol contracts (OpenID Connect, SAML 2.0) live in their own projects
under the `Protocols` branch, and concrete token-document behavior lives in the
`Token` branch. Every one of those projects depends on this root package and
none defines a parallel foundational identity abstraction. See the area
[README](../../README.md) for the full project map and the family
[DESIGN.md](DESIGN.md) for the keystone design rationale.

## Status

Epic `[L01.01.12]` (#105) is in delivery. This root package currently ships:

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

The shared protocol abstractions (`[L01.01.12.03]`) ship in the
`Assimalign.Cohesion.IdentityModel.Protocols` project, and the OpenID Connect
contracts (`[L01.01.12.04]`) in
`Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect` — each with its own
`docs/`. Remaining features land in dependency order: SAML branch, then
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

None (BCL only). The rest of the family depends one-way on this package (see
the README for the branch diagram).

## Test coverage

`Assimalign.Cohesion.IdentityModel.Tests` contains the family-level guards plus
the root domain-model suites:

- `IdentityModelFamilyBoundaryTests` — dependency-direction guards across all
  six family assemblies: the root references no Cohesion assemblies, each
  branch references only its parent chain, the protocol and token branches
  never cross-reference, siblings never reference each other, and no family
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

Protocol-contract and compliance-fixture suites live in the respective protocol
projects' test assemblies.
