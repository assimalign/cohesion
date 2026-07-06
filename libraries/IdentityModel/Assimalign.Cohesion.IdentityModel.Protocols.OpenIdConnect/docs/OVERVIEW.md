# Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect

## Summary

The OpenID Connect contract branch of the Cohesion IdentityModel family. It
models the OpenID Connect / OAuth 2.0 protocol surface as descriptive
contracts on top of the shared `…Protocols` abstractions:

- **Metadata**: `OpenIdConnectProviderMetadata` (Discovery 1.0) and
  `OpenIdConnectClientMetadata` (registered client), with typed wire members
  projected into the shared endpoint list, plus the standalone
  `OpenIdConnectClientRegistrationRequest`.
- **Messages**: authorization and token request/response contracts
  (Authorization Code with PKCE as the primary flow), riding the shared
  envelopes with `ClientId`/`iss` aliasing.
- **Token contracts**: `OpenIdConnectIdToken`, `OpenIdConnectUserInfo`, and
  `OpenIdConnectLogoutToken` — single-source claim surfaces with pure
  data-rule `Validate()` methods (Core §3.1.3.7, Back-Channel Logout §2.6);
  crypto is left to the JWT package.
- **Logout**: RP-initiated and back-channel logout requests composing the
  shared logout semantics, plus the pinned wire-only subject-identifier recipe
  that keeps single-logout correlation from forking.
- **Constants**: scopes, response types (with order-insensitive matching),
  response modes, grant types, parameter names, prompt/display values, error
  codes, claim types, event types, and OIDC-minted validation codes.

## Status

Delivered by feature `[L01.01.12.04]` (#596), including
specification-oriented compliance fixtures parsed through a reflection-free,
AOT-safe `System.Text.Json` path.

## Dependencies

- `Assimalign.Cohesion.IdentityModel.Protocols` (and transitively the root
  canonical model). BCL otherwise; no `Microsoft.Extensions.*`, no transport
  or HTTP-client dependencies.

## Scope

Descriptive, not executable. It does NOT fetch discovery documents, make HTTP
calls, parse compact JWTs, or verify signatures — those are the JSON Web Token
package's and future implementation packages' concern. See the family
[DESIGN.md](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) —
"OpenID Connect branch decisions" — for the design rationale.
