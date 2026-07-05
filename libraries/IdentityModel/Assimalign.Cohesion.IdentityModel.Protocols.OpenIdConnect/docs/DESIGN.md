# Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect — Design

## Design intent

The OpenID Connect contract branch: descriptive contracts for the OpenID
Connect / OAuth 2.0 protocol surface, derived from the shared `…Protocols`
base. It is one of the per-protocol projects that hang off that base, so OpenID
Connect can evolve without touching the shared abstractions or the other
protocol branches.

The load-bearing design rationale for every decision — the guards-stop-at-
artifact-identity rule, envelope aliasing, single-source claim collections, the
pinned subject-identifier recipe, the Core §3.1.3.7 validation severity map,
and the split with the JWT package — lives in the family keystone,
[`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md),
under **"OpenID Connect branch decisions"**. This document records only what is
specific to the project boundary; do not duplicate the keystone here.

## Dependency and internals posture

- Depends on `Assimalign.Cohesion.IdentityModel.Protocols` (transitively the
  root canonical model). Shared and root types resolve by namespace nesting.
- Uses two shared-internal seams via `InternalsVisibleTo`: the root's
  `ModelSnapshot` (descriptor materialization) and `…Protocols`'
  `ProtocolEndpoint.IsValidLocation` (the endpoint well-formedness rule used by
  metadata projection and redirect-URI validation).

## Boundary with the JSON Web Token package

This project owns the OpenID Connect *protocol* contracts and *data-rule*
validation. It does not parse compact JWTs or run cryptography. The JSON Web
Token package (`…Token.JsonWebToken`) materializes the token claim-surface
contracts here (`OpenIdConnectIdToken`, `OpenIdConnectLogoutToken`) from parsed
JWTs and owns signature / `at_hash` / `c_hash` verification — the one crypto
concern. Each Core §3.1.3.7 rule therefore has exactly one owner.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>`. No reflection or serializers on any
shipped path; the compliance-fixture tests use `System.Text.Json`'s
reflection-free `JsonDocument` reader, which is the same AOT-safe path
implementation packages use to materialize these contracts.

## Non-goals

Discovery/JWKS retrieval, HTTP calls, compact-JWT parsing, signature
verification, and full protocol-flow orchestration are out of scope — they
belong to the JWT package and to future executable implementation packages that
depend on this contract branch plus the transport/Security areas.
