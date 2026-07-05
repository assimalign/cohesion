# Assimalign.Cohesion.IdentityModel.Protocols.Saml — Design

## Design intent

The SAML 2.0 contract branch: descriptive contracts for the SAML protocol
surface, derived from the shared `…Protocols` base. It is one of the
per-protocol projects that hang off that base, so SAML can evolve without
touching the shared abstractions or the other protocol branches, and is built
and tested in isolation.

The load-bearing design rationale for every decision — the assertion-as-token
contract, the preserved SAML structure and typed encrypted markers, the
audience AND-across/OR-within rule, the existential bearer-confirmation check,
caller-instant clock skew, the raw-attribute-name claim projection, the pinned
NameID-to-`SubjectIdentifier` recipe that keeps single-logout correlation from
forking, and the role-stamped metadata projection — lives in the family
keystone,
[`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md),
under **"SAML branch decisions"**. This document records only what is specific
to the project boundary; do not duplicate the keystone here.

## Dependency and internals posture

- Depends on `Assimalign.Cohesion.IdentityModel.Protocols` (transitively the
  root canonical model). Shared and root types resolve by namespace nesting.
- Uses two shared-internal seams via `InternalsVisibleTo`: the root's
  `ModelSnapshot` (descriptor materialization, e.g. `SamlAuthnRequest`'s string
  lists) and `…Protocols`' `ProtocolEndpoint.IsValidLocation` (endpoint
  well-formedness). Both are exposed to this assembly from the projects that own
  them, per the family's "adding a protocol package" recipe.

## Boundary with the SAML token package

This project owns the SAML *protocol* contracts and *data-rule* validation. It
does not read or write SAML XML, verify signatures, or decrypt encrypted
elements. The SAML token package (`…Token.Saml`) materializes the assertion
contract here from parsed XML and owns signature verification and decryption of
the `SamlEncryptedElement` markers — the crypto concerns. Each SAML Core rule
therefore has exactly one owner. Every signable element retains its verbatim
`RawXml` (the assertion subtree and the response element separately) so the
token package can re-verify assertion-level and response-level signatures
independently.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>`. Plain-old CLR object models — no
serializers, no reflection on any shipped path, no runtime code generation. The
contracts carry raw SAML XML as opaque strings rather than modeling an XML
object graph, so nothing here needs an XML reader; parsing is the token
package's concern.

## Non-goals

SAML XML reading/writing, signature verification, `EncryptedID` /
`EncryptedAssertion` / `EncryptedAttribute` decryption, artifact resolution, and
full SSO/SLO flow orchestration are out of scope — they belong to the SAML token
package and to future executable implementation packages that depend on this
contract branch plus the Security/transport areas. Binding descriptors describe
transports; they never execute them.
