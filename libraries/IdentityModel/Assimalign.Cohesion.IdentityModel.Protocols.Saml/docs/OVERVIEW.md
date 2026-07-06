# Assimalign.Cohesion.IdentityModel.Protocols.Saml

## Summary

The SAML 2.0 contract branch of the Cohesion IdentityModel family. It models
the SAML protocol surface as descriptive contracts on top of the shared
`…Protocols` abstractions:

- **Assertion contract**: `SamlAssertion` (the SAML counterpart to the OIDC ID
  token) with real SAML structure preserved — `SamlSubject`,
  `SamlSubjectConfirmation`/`Data`, `SamlAuthnStatement`/`SamlAuthnContext`,
  `SamlConditions`, `SamlAttributeStatement` (reusing the canonical
  `IdentityAttribute`), and `SamlNameId`. A pure data-rule `Validate()` covers
  SAML Core and the Web Browser SSO profile; a single-source claim collection
  is built at materialization with SAML provenance.
- **Messages**: `SamlAuthnRequest` (§3.4.1) and `SamlResponse` (§3.3.3) with
  envelope validation, plus single-logout `SamlLogoutRequest` /
  `SamlLogoutResponse` composing the shared logout semantics.
- **Entity metadata**: `SamlEntityMetadata` (an `EntityDescriptor`) whose role
  descriptors are the authoritative per-role view and whose inherited flat
  endpoint/key lists are the role-stamped projection, so dual-role entities stay
  unambiguous.
- **Encrypted content**: typed `SamlEncryptedElement` markers for `EncryptedID`,
  `EncryptedAssertion`, and `EncryptedAttribute` — preserved, never dropped, for
  the token package to open.
- **Constants**: NameID formats (forwarding to the canonical
  `SubjectIdentifierFormats`), binding URIs with a single map onto the neutral
  `ProtocolBinding` vocabulary, status codes, confirmation methods, attribute
  name formats, authn-context classes, and SAML-minted validation codes.

## Status

Delivered by feature `[L01.01.12.05]` (#600), including a SAML Web Browser SSO
profile conformance matrix.

## Dependencies

- `Assimalign.Cohesion.IdentityModel.Protocols` (and transitively the root
  canonical model). BCL otherwise; no `Microsoft.Extensions.*`, no transport,
  HTTP-client, or XML-serialization dependencies.

## Scope

Descriptive, not executable. It does NOT parse or emit SAML XML, verify
signatures, decrypt encrypted elements, or orchestrate SSO/SLO flows — those are
the SAML token package's and future implementation packages' concern. See the
family [DESIGN.md](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) —
"SAML branch decisions" — for the design rationale.
