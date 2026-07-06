# Assimalign.Cohesion.IdentityModel.Protocols.Saml

> Assembly reference. Public API surface of the SAML 2.0 contract branch.

This assembly models the SAML 2.0 protocol as descriptive, non-cryptographic contracts: assertions, the Web Browser SSO and Single Logout messages, and entity metadata, all built on the family's shared protocol abstractions (`ProtocolRequest`, `ProtocolResponse`, `ProtocolMetadata`). Immutable models materialize from mutable descriptors, preserve wire fidelity (verbatim XML octets, encrypted-element markers, NameID qualifiers) so the SAML token package can perform signature verification and decryption, and own the pure data-validation rules of SAML Core and the Web Browser SSO profile. Constant classes carry the OASIS wire vocabularies (bindings, status codes, NameID formats, confirmation methods), and a pinned extension recipe lifts a `SamlNameId` into the family's canonical `SubjectIdentifier` so login and logout legs correlate.

## Public types

### Assertion model

| Type | Role |
| --- | --- |
| `SamlAssertion` | Immutable SAML assertion contract; builds provenance-stamped claims and validates SAML Core / Web Browser SSO data rules. |
| `SamlAssertionDescriptor` | Mutable description of assertion contents before materialization into `SamlAssertion`. |
| `SamlAssertionValidationOptions` | Relying-party context (instant, skew, issuer, audience, recipient, `InResponseTo`, profile toggles) for assertion validation. |
| `SamlSubject` | The principal an assertion is about: optional NameID, confirmations, or an encrypted identifier marker. |
| `SamlNameId` | Wire-faithful `NameID` with format, both qualifiers, and the SP-provided identifier. |
| `SamlSubjectConfirmation` | A confirmation mechanism: method URI, optional confirming NameID, and constraining data. |
| `SamlSubjectConfirmationData` | Constraints on a confirmation: recipient, temporal window, `InResponseTo`, address, `KeyInfo` XML. |
| `SamlAuthnStatement` | Statement that the subject authenticated at an instant, with session index and locality. |
| `SamlAuthnContext` | The authentication event's context: class/declaration references, inline declaration, authenticating authorities. |
| `SamlAttributeStatement` | Attributes asserted about the subject as canonical `IdentityAttribute` values, plus encrypted-attribute markers. |
| `SamlConditions` | Assertion reliance constraints: temporal window, audience restrictions (AND-across/OR-within), one-time-use, proxy restriction. |
| `SamlEncryptedElement` | Verbatim XML of an encrypted element the descriptive layer cannot open; the token package decrypts it. |

### Protocol messages

| Type | Role |
| --- | --- |
| `SamlAuthnRequest` | Immutable `AuthnRequest`: ACS target, response binding, `ForceAuthn`/`IsPassive`, NameID policy, requested contexts; validates structure. |
| `SamlAuthnRequestDescriptor` | Mutable description of `AuthnRequest` contents before materialization. |
| `SamlResponse` | Immutable `Response` carrying assertions and encrypted-assertion markers; validates status, correlation, destination, assertion presence. |
| `SamlResponseDescriptor` | Mutable description of `Response` contents before materialization; status rides the inherited base. |
| `SamlResponseValidationOptions` | Receiving context (`ExpectedInResponseTo`, `ExpectedDestination`) for response envelope validation. |
| `SamlLogoutRequest` | Immutable `LogoutRequest`; correlates single logout on issuer plus provider session identifiers. |
| `SamlLogoutRequestDescriptor` | Mutable description of `LogoutRequest` contents; `Apply` derives the shared subject and session identifiers. |
| `SamlLogoutResponse` | Immutable `LogoutResponse`; outcome (including partial logout) carried by the inherited status. |
| `SamlLogoutResponseDescriptor` | Mutable description of `LogoutResponse` contents before materialization. |

### Entity metadata

| Type | Role |
| --- | --- |
| `SamlEntityMetadata` | Immutable `EntityDescriptor`: typed role descriptors plus role-stamped flat endpoint/key projection; validates conformance and expiry. |
| `SamlEntityMetadataDescriptor` | Mutable description of `EntityDescriptor` contents before materialization. |
| `SamlRoleDescriptor` | One role's endpoints, keys, supported NameID formats, signing policy, and expiry. |
| `SamlOrganization` | Metadata `Organization`: name, display name, URL. |
| `SamlContactPerson` | Metadata `ContactPerson`: contact type, company, names, email. |

### Wire vocabularies

| Type | Role |
| --- | --- |
| `SamlConstants` | Core constants: SAML version, assertion/protocol/metadata XML namespaces, consent identifiers. |
| `SamlBindings` | Binding URIs and the single mapping onto the family's `ProtocolBinding` vocabulary. |
| `SamlNameIdFormats` | NameID format URIs, forwarding to the canonical `SubjectIdentifierFormats` literals. |
| `SamlStatusCodes` | Top-level and second-level status code URIs (SAML Core §3.2.2.2). |
| `SamlConfirmationMethods` | Subject confirmation method URIs: bearer, holder-of-key, sender-vouches. |
| `SamlAttributeNameFormats` | Attribute name format URIs: unspecified, URI, basic. |
| `SamlAuthnContextClasses` | Common authentication context class URIs (password, Kerberos, X.509, smartcard, TLS client). |
| `SamlValidationCodes` | SAML-minted validation diagnostic codes; cross-protocol concepts use the shared `ProtocolValidationCodes`. |

### Extensions

| Type | Role |
| --- | --- |
| `SamlSubjectExtensions` | The pinned recipe lifting a `SamlNameId` into the canonical `SubjectIdentifier` for both login and logout legs. |

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
