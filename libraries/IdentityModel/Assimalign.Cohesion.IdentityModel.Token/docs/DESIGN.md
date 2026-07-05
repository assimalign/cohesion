# Assimalign.Cohesion.IdentityModel.Token — Design

## Design intent

The protocol-neutral token/assertion normalization layer: it models what it
means to be "a token" — issuer, subject, audiences, temporal validity,
authentication context, normalized claims, provenance — independent of wire
format, so an OpenID Connect ID token and a SAML assertion present the same
canonical surface. It is one of the two branches off the root anchor (the other
being the protocol contracts); the concrete JWT and SAML token packages derive
from it.

The load-bearing design rationale — reusing the root canonical types instead of
duplicating them, `Claims` as the authoritative record with the typed members as
its projection, the two-issuer distinction, document-format vs protocol axes, a
`Validate()` method instead of a validator seam, no reader/writer seam, and the
branch-local validation result — lives in the family keystone,
[`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md),
under **"Token normalization decisions"**. This document records only what is
specific to the project boundary; do not duplicate the keystone here.

## Dependency and internals posture

- Depends on `Assimalign.Cohesion.IdentityModel` (the root canonical model)
  only. It does **not** reference the protocol branch (`…Protocols`); the
  boundary tests enforce that independence.
- It is **not** granted access to the root's internal `ModelSnapshot` helper
  (unlike the protocol branches). The token base therefore reproduces the
  fail-closed property-snapshot rule inline — a blank property name or an
  undefined value is rejected at materialization — and builds its immutable
  `Claims` through the public root `IdentityClaimCollection`.

## Boundary with the concrete token packages

This project owns the *neutral normalized shape* and the *format-agnostic*
`Validate()` rules (issuer, audience, primary temporal window). It does not
parse or serialize any wire format, run cryptography, or model format-specific
structure. The JWT package (`…Token.JsonWebToken`) and SAML package
(`…Token.Saml`) derive from `IdentityToken`, pin their `IdentityTokenKind`, add
their format-specific surface (JOSE header, assertion conditions), and own
signature verification and the format-specific validity windows. During
`[L01.01.12.06]` those two packages keep `object?`-typed header/condition
dictionaries; migrating them to `IdentityClaimValue` is deferred to their
grade-up features (#608, #612).

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>`. Plain-old CLR object models, no
serializers, no reflection, no runtime code generation. Removing the neutral
layer's `object?` value bags in favor of the typed `IdentityClaimValue` is part
of the AOT posture, not only an alignment tidy-up. Dropping the validator/reader
interfaces in favor of a sealed base method and sealed result types is strictly
more source-generation-friendly (concrete types, no interface dispatch to
specialize).

## Non-goals

Compact-JWT and assertion-XML parsing, serialization, signature verification,
and format-specific validity semantics are out of scope — they belong to the
concrete token packages and to future executable layers. A neutral token reader
or writer seam is deliberately not shipped; it would abstract behavior that is
inherently format-specific with no polymorphic caller.
