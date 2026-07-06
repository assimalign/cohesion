# Assimalign.Cohesion.IdentityModel.Token.Saml — Design

## Design intent

The concrete SAML 2.0 assertion token layer: it takes the neutral token base to the
assertion-grade fidelity downstream services need — typed statements/conditions/subject
confirmation/NameID — normalized onto the root canonical model, so a SAML-asserted principal is
consumable through the same `IIdentityToken` surface as a JWT. It derives from `IdentityToken`
and pins its document format.

The load-bearing rationale — the re-model-vs-reuse line, the re-minted NameID recipe and its
drift guard, `Claims`-as-authoritative, and the validation split — is recorded in the family
keystone,
[`Assimalign.Cohesion.IdentityModel/docs/DESIGN.md`](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md),
under **"SAML token package decisions"**. This document records the project-boundary specifics.

## Re-model vs reuse (branch independence)

The token branch cannot reference the protocol branch (`…Protocols.Saml`), which already models
the assertion richly. The line drawn:

- **Reuse root** where root already owns the concept: the subject is a root `SubjectIdentifier`;
  attributes are root `IdentityAttribute` and become root claims; the authentication statement
  (instant/class/session indexes/session expiry, and subject locality via Properties) is the root
  `AuthenticationContext` — **no** parallel authentication-statement type is minted. NameID
  **format** URIs reuse the root `SubjectIdentifierFormats` (never a copy), because the format
  participates in `SubjectIdentifier` equality and a second literal would be a drift hazard.
- **Mint a branch-local mirror** only where root cannot hold the SAML shape losslessly:
  `SamlNameId` (qualifiers), `SamlConditions` (per-restriction audience grouping + AND-across
  rule), `SamlSubjectConfirmation`/`SamlSubjectConfirmationData` (the bearer window), and
  `SamlEncryptedElement` (the encrypted markers). The bearer confirmation-**method** URI is
  minted (root does not own SAML protocol vocabulary), unlike the formats.
- **Re-mint the NameID→`SubjectIdentifier` recipe** field-for-field from the protocol branch. Two
  drift hazards result — the recipe and the bearer method — both pinned equal to the protocol
  branch by a guard in the root test project (the only assembly that may reference both branches).

## `Claims` is authoritative; the typed members are its projection

Claims materialize by the same rule as the protocol branch's `SamlAssertion.BuildClaims`: `sub`
from the NameID (saml2 provenance with the NameID format on `originalNameFormat` —
`originalType` is reserved for original wire names), and one claim per attribute **value**
keyed by the raw SAML attribute name with `originalNameFormat`/`originalFriendlyName` provenance.
Authentication/session data is **not** forced into claims — it flows to `AuthenticationContext`.
This keeps a JWT- and a SAML-normalized principal resolving to the same canonical shape.

## Validation split

`SamlToken.Validate` owns the token substrate; the protocol branch's `SamlAssertion.Validate`
owns the full SAML Core / Web Browser SSO profile. The token substrate:

- composes the neutral base issuer + temporal rules (the temporal window comes from `Conditions`,
  never from a bearer confirmation);
- evaluates the SAML **audience** rule through `SamlConditions.IsAudienceSatisfied` (AND across
  restrictions, empty restriction = unsatisfiable) — **not** the base's flat union;
- checks the **bearer confirmation-data window** existentially (`NotOnOrAfter` present + future
  with skew, `NotBefore` forbidden, and `Recipient`/`InResponseTo` equality only when the caller
  supplies them).

It deliberately does **not** impose the profile's require-a-bearer posture (that is
`SamlAssertion.Validate`'s), and the temporal overlap with the protocol branch is by design —
document substrate versus protocol profile — and must not be consolidated.

## Compatibility matrix

| Capability | Status |
|---|---|
| Typed assertion structure (NameID, conditions, subject confirmation) | Implemented |
| Normalization onto root (subject/claims/authn-context/temporal) | Implemented |
| Audience AND-across rule (`SamlConditions.IsAudienceSatisfied`) | Implemented |
| Bearer confirmation-data window validation | Implemented |
| Encrypted element markers (`EncryptedID`/`EncryptedAssertion`/`EncryptedAttribute`) | Preserved (marker only) |
| SAML XML parse / serialize | Deferred (future package) |
| Assertion signature verification | Seam only — via `AssertionXml`/`RawData` |
| Encrypted-element decryption | Deferred (keyed seam) |
| Full SAML Core / Web Browser SSO profile validation | Out of scope (`…Protocols.Saml.SamlAssertion.Validate`) |

A successful `Validate` means "data + bearer-window rules passed", never "signature verified".

## AOT posture

`IsAotCompatible=true` is inherited from `libraries/Directory.Build.props` and enforced at build
time by the trimming/AOT analyzers (not by a test). Because F8 defers all XML parsing, the package
is pure immutable POCO construction from a descriptor plus deterministic in-memory validation — no
`System.Text.Json`, no reflection, no serializer, nothing package-specific to annotate. The family
boundary test additionally guards the no-`Microsoft.Extensions.*` rule for this assembly.

## Non-goals

SAML XML reading/writing, assertion signature verification, and `EncryptedID`/`EncryptedAssertion`/
`EncryptedAttribute` decryption are out of scope — deferred parse/keyed seams. Full SAML Core
profile validation belongs to the protocol branch.
