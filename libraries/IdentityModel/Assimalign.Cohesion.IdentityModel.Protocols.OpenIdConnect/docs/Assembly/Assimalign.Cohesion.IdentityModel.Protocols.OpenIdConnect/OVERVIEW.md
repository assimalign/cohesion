# Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect

> Assembly reference. Public API surface of the OpenID Connect contract branch of the Cohesion IdentityModel family.

This assembly models the OpenID Connect wire contracts as immutable, descriptor-built artifacts on top of the shared `Assimalign.Cohesion.IdentityModel.Protocols` abstractions: provider and client metadata, authorization and token endpoint messages, the ID token and logout token claim surfaces, UserInfo, and the RP-initiated and back-channel logout legs. Materialization guards only structural identity; specification conformance is reported by each artifact's `Validate` method as diagnostics, so non-conformant wire data stays representable and diagnosable. Transport, compact-JWT parsing, and cryptographic verification are deliberately out of scope and belong to adjacent packages.

## Public types

### Provider and client metadata

| Type | Role |
| --- | --- |
| `OpenIdConnectProviderMetadata` | Immutable OP discovery document; typed wire members plus projected base endpoint list; `Validate` reports Discovery 1.0 conformance. |
| `OpenIdConnectProviderMetadataDescriptor` | Mutable input shape for a provider discovery document; base endpoint list reserved for extension endpoints. |
| `OpenIdConnectClientMetadata` | Immutable registered client (relying party) record; logout and JWK Set URIs projected as base endpoints. |
| `OpenIdConnectClientMetadataDescriptor` | Mutable input shape for a registered client record; also the registration request's input shape. |
| `OpenIdConnectClientRegistrationRequest` | Dynamic client registration request (RFC 7591): client metadata submitted before a `client_id` exists. |
| `OpenIdConnectEndpointKinds` | Well-known `ProtocolEndpointKind` values for the typed OpenID Connect endpoints. |

### Authorization endpoint

| Type | Role |
| --- | --- |
| `OpenIdConnectAuthorizationRequest` | Immutable authentication request; `Validate` checks `openid` scope, redirect URI, nonce, PKCE, and prompt rules. |
| `OpenIdConnectAuthorizationRequestDescriptor` | Mutable input shape for an authorization request. |
| `OpenIdConnectAuthorizationResponse` | Immutable authorization response holding code and implicit/hybrid artifacts; `Validate` checks promised artifacts and mix-up defenses. |
| `OpenIdConnectAuthorizationResponseDescriptor` | Mutable input shape for an authorization response; carries the RFC 9207 `iss` parameter on the envelope. |
| `OpenIdConnectAuthorizationResponseValidationOptions` | Expected state, issuer, and response type an authorization response is validated against. |

### Token endpoint

| Type | Role |
| --- | --- |
| `OpenIdConnectTokenRequest` | Immutable token endpoint request; client secrets are never modeled, client assertions are. |
| `OpenIdConnectTokenRequestDescriptor` | Mutable input shape for a token request. |
| `OpenIdConnectTokenResponse` | Immutable token endpoint response; every member nullable so `Validate` can diagnose non-conformant providers. |
| `OpenIdConnectTokenResponseDescriptor` | Mutable input shape for a token response. |

### ID token, UserInfo, and claims

| Type | Role |
| --- | --- |
| `OpenIdConnectIdToken` | Claims surface of an ID token; `Validate` owns the Core §3.1.3.7 data rules. |
| `OpenIdConnectIdTokenDescriptor` | Mutable claim-surface input for an ID token; typed members plus non-colliding extension claims. |
| `OpenIdConnectIdTokenValidationOptions` | Expected issuer, audience, nonce, and clock inputs for ID token data-rule validation. |
| `OpenIdConnectUserInfo` | UserInfo response (Core §5.3); subject presence and match are `Validate` diagnostics, not guards. |
| `OpenIdConnectUserInfoDescriptor` | Mutable input shape for a UserInfo response. |
| `OpenIdConnectClaimsSource` | Unresolved aggregated or distributed claims reference (Core §5.6.2), preserved for external resolution. |
| `OpenIdConnectSubjectExtensions` | `GetSubjectIdentifier` extensions lifting ID and logout token subjects into the canonical `SubjectIdentifier`. |

### Logout

| Type | Role |
| --- | --- |
| `OpenIdConnectLogoutRequest` | RP-initiated logout request (RP-Initiated Logout 1.0 §2). |
| `OpenIdConnectLogoutRequestDescriptor` | Mutable input shape for an RP-initiated logout request. |
| `OpenIdConnectLogoutResponse` | Post-logout redirect response carrying only the `state` echo and outcome status. |
| `OpenIdConnectLogoutResponseDescriptor` | Mutable input shape for an RP-initiated logout response. |
| `OpenIdConnectBackChannelLogoutRequest` | Back-channel logout token delivery; shared logout members drive protocol-neutral single-logout orchestration. |
| `OpenIdConnectBackChannelLogoutRequestDescriptor` | Mutable input shape; `Apply` populates the shared logout members from a parsed logout token. |
| `OpenIdConnectLogoutToken` | Claims surface of a back-channel logout token; `Validate` owns the §2.6 data rules. |
| `OpenIdConnectLogoutTokenDescriptor` | Mutable claim-surface input for a logout token. |
| `OpenIdConnectLogoutTokenValidationOptions` | Expected issuer, audience, and clock inputs for logout token data-rule validation. |

### Wire vocabulary

| Type | Role |
| --- | --- |
| `OpenIdConnectScopes` | Scope values registered by Core 1.0 (`openid`, `profile`, and peers). |
| `OpenIdConnectResponseTypes` | Atomic `response_type` values plus the order-insensitive `Matches` comparison and `Split` helper. |
| `OpenIdConnectResponseModes` | `response_mode` values and their mapping onto the family's `ProtocolBinding` vocabulary. |
| `OpenIdConnectGrantTypes` | `grant_type` values relevant to OpenID Connect flows. |
| `OpenIdConnectPromptValues` | `prompt` parameter values (Core §3.1.2.1). |
| `OpenIdConnectDisplayValues` | `display` parameter values (Core §3.1.2.1). |
| `OpenIdConnectParameterNames` | Wire parameter names for transports that render or parse query strings and form bodies. |
| `OpenIdConnectClaimTypes` | Claim types minted by OpenID Connect; JWT-core registered names stay on `IdentityClaimTypes`. |
| `OpenIdConnectErrorCodes` | Wire `error` codes from RFC 6749 and OpenID Connect, consumed through `ProtocolResponseStatus.Code`. |
| `OpenIdConnectEventTypes` | Security event URIs for the `events` claim (back-channel logout). |
| `OpenIdConnectValidationCodes` | OpenID-Connect-minted validation diagnostic codes; cross-protocol codes stay on `ProtocolValidationCodes`. |

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
