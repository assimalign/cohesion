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
  and the internal `ModelSnapshot` helper is compiled locally from the single
  `Assimalign.Cohesion.IdentityModel/shared/ModelSnapshot.cs` source.
- `ProtocolEndpoint` keeps its single public CLR identity in this assembly.
  Its string-to-boolean location validation is factored into internal static
  `EndpointLocation`, held in this project's own `shared/` folder and linked into
  this assembly and OpenIdConnect. Only that stateless algorithm is duplicated at
  compilation; endpoint instances are never compiled into another assembly.
- Each assembly declares its own `CohesionSharedSource` items, so the csproj a
  reader opens states which source it compiles; there is no area
  `Directory.Build.targets`. The helpers remain implementation details with no added public surface and no
  shipped-to-shipped friend grants. Their retained namespaces are a documented,
  narrowly scoped exception to namespace alignment. See the keystone's
  "Shared implementation source" and "Adding a protocol package" sections.

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
