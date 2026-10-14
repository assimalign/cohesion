# Assimalign.Cohesion.IdentityHub.Models — Design

## Compatibility boundary

The existing mutable tenant, user, group, application, and role DTOs predate the canonical
IdentityModel family. They remain in place so current consumers keep their public shapes and
generated ULID value types. New identity semantics are additive links to canonical contracts:
users and service principals expose `IIdentitySubject`, while application credential records
expose `IdentityCredential`.

## Generated string value-type compatibility (#1472)

Regeneration removes `ToString(string?, IFormatProvider?)` from `Username` and `Domain`.
Both types exposed that overload in `10.0.0-preview.1`, so its removal is a source and binary
breaking change under the preview compatibility policy (HTTP/Web program decision 15).
Replace calls such as `username.ToString(format, provider)` with `username.ToString()` and
recompile consumers. The removed overload did not apply formatting or culture to initialized
string values; `ToString()` preserves their text and now returns an empty string for `default`.

## Canonical identity ownership

IdentityModel owns subjects, identifiers, claims, credentials, authentication sessions and
results. Its token and protocol branches own JWT, JOSE/JWK, OIDC, and SAML contracts. This
project does not mirror those types. IdentityHub persistence and workflow code composes the
canonical immutable objects alongside its service-specific tenant identifiers.

## Dependencies and AOT posture

Core remains required by the generated ULID-backed legacy identifiers. IdentityModel supplies
the canonical identity contracts. Both are AOT-compatible; this project adds no reflection,
runtime discovery, or reflection-based serialization.

## Non-goals

- Replacing or renaming legacy DTO members in this compatibility change.
- Defining token, claim, authentication-session, validation, or JWK models.
- Implementing IdentityHub storage or protocol behavior.
