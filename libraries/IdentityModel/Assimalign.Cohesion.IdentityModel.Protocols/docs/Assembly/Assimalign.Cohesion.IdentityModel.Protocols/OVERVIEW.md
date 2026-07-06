# Assimalign.Cohesion.IdentityModel.Protocols

> Assembly reference. Public API surface of the shared authentication-protocol abstractions.

Assimalign.Cohesion.IdentityModel.Protocols provides the protocol-neutral abstractions shared by every authentication protocol branch in the IdentityModel family: message and metadata envelopes, endpoint and key descriptions, party references, and validation currency. Protocol branches (OpenID Connect, SAML 2.0) derive concrete types from these bases and add their spec surface on top; the shared core carries only what is genuinely common to both. The model is descriptive and data-only — it never connects to endpoints, performs cryptography, or executes exchanges — and immutable model types are materialized from mutable descriptors.

## Public types

### Protocol vocabulary

| Type | Role |
| --- | --- |
| `ProtocolRole` | Open vocabulary struct for party roles; ships identity-provider, relying-party, OAuth server, issuer, and audience values. |
| `ProtocolBinding` | Open vocabulary struct naming transport shapes (redirect, POST, fragment, artifact, SOAP, back-channel) one canonical spelling each. |
| `ProtocolEndpointKind` | Open vocabulary struct for endpoint purpose; well-known kinds are minted by the owning protocol branch. |

### Parties and exchanges

| Type | Role |
| --- | --- |
| `ProtocolParty` | Lightweight party reference (identifier plus role) with ordinal equality, used by trust registries and diagnostics. |
| `ProtocolExchange` | Describes the two legs of one exchange: request endpoint and optional response endpoint (null means back-channel). |

### Endpoints and keys

| Type | Role |
| --- | --- |
| `ProtocolEndpoint` | Immutable endpoint description: kind, wire-exact absolute-URI locations, binding, role scope, index, tri-state default flag. |
| `ProtocolEndpointDescriptor` | Mutable descriptor materialized into a `ProtocolEndpoint`. |
| `ProtocolKey` | Immutable published-key description (certificates, algorithms, use, role scope) with `CanSign`/`CanEncrypt` helpers; never does cryptography. |
| `ProtocolKeyDescriptor` | Mutable descriptor materialized into a `ProtocolKey`. |
| `ProtocolKeyUse` | Declared key-use restriction enum; `Unspecified` preserves the SAML/JWK absent-`use` "any purpose" wire semantic. |

### Entity metadata

| Type | Role |
| --- | --- |
| `ProtocolMetadata` | Abstract base for published entity metadata: required entity identifier, roles, endpoints, keys, validity, raw document. |
| `ProtocolMetadataDescriptor` | Abstract mutable descriptor paired with `ProtocolMetadata`; branches derive one per concrete metadata type. |

### Message envelopes

| Type | Role |
| --- | --- |
| `ProtocolMessage` | Abstract transport-agnostic message envelope: identifier, issuer, destination, issue instant, correlation state, raw message. |
| `ProtocolMessageDescriptor` | Abstract mutable descriptor paired with `ProtocolMessage`. |
| `ProtocolRequest` | Abstract shared envelope of a protocol request message. |
| `ProtocolRequestDescriptor` | Abstract mutable descriptor paired with `ProtocolRequest`. |
| `ProtocolResponse` | Abstract shared envelope of a protocol response; requires a status and carries `InResponseTo` correlation. |
| `ProtocolResponseDescriptor` | Abstract mutable descriptor paired with `ProtocolResponse`. |
| `ProtocolResponseStatus` | Normalized response outcome covering the OAuth error triplet and SAML nested status structure; outcome stored, never inferred. |

### Logout

| Type | Role |
| --- | --- |
| `ProtocolLogoutRequest` | Abstract logout request: optional subject, provider session identifiers, reason, and not-on-or-after instant. |
| `ProtocolLogoutRequestDescriptor` | Abstract mutable descriptor paired with `ProtocolLogoutRequest`. |
| `ProtocolLogoutResponse` | Abstract logout response; outcome (including partial logout) rides the inherited status. |
| `ProtocolLogoutResponseDescriptor` | Abstract mutable descriptor paired with `ProtocolLogoutResponse`. |

### Validation

| Type | Role |
| --- | --- |
| `ProtocolValidationResult` | Immutable validation outcome; succeeds exactly when no error-severity diagnostics exist, so "succeeded with errors" is unconstructible. |
| `ProtocolValidationDiagnostic` | One normalized validation finding: severity, machine-readable code, message, optional member, typed properties. |
| `ProtocolValidationSeverity` | Finding severity enum (`Error`, `Warning`, `Information`); zero value is `Error` so leaks fail closed. |
| `ProtocolValidationCodes` | Constants class of cross-protocol diagnostic codes shared by OpenID Connect and SAML validators. |

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone design](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
