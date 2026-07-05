# Assimalign.Cohesion.IdentityModel.Protocols — Design

## Design intent

This project is the shared base of the protocol contract branch: the concepts
that recur across authentication protocols, factored out so each protocol
project (OpenID Connect, SAML 2.0, and future protocols) derives from one place
instead of re-inventing party roles, metadata shapes, message envelopes,
response status, validation results, and binding descriptors. Keeping the base
separate is what lets protocols expand — and new protocols be added — without
touching the shared abstractions or the other protocols, and lets each protocol
be built and tested in isolation.

The full, load-bearing design rationale for every type here lives in the family
keystone, [`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md),
under **"Shared protocol abstraction decisions"**. This document records only
what is specific to the project boundary; do not duplicate the keystone here.

## Dependency and internals posture

- Depends only on `Assimalign.Cohesion.IdentityModel`. The canonical model's
  types (`IdentityClaimValue`, `SubjectIdentifier`, `AuthenticationProtocol`,
  `IdentityModelException`) resolve by namespace nesting — no `using` needed —
  and the shared internal `ModelSnapshot` helper is reachable because the root
  project grants `InternalsVisibleTo` to this assembly.
- Exposes its own internal helper `ProtocolEndpoint.IsValidLocation` (the
  wire-exact absolute-URI rule) to the protocol branches via `InternalsVisibleTo`.
  When a new protocol project is added it goes on both `InternalsVisibleTo`
  lists (root's, for `ModelSnapshot`; this project's, for `IsValidLocation`) —
  see the keystone's "Adding a protocol package".

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` via the area build props. Pure
contract types over the BCL: no reflection, no serializers, no runtime code
generation. The open-vocabulary structs (`ProtocolRole`, `ProtocolBinding`,
`ProtocolEndpointKind`) are trim-safe value types whose `default` is a
functional `Unknown`.

## Non-goals

The keystone's non-goals apply. Specifically here: no transport execution
(binding descriptors describe transports, never open them), no cryptography
(keys are descriptors), and no protocol-specific contracts — those belong in
the protocol branch projects.
