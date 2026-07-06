# Assimalign.Cohesion.IdentityModel.Token.Saml

## Summary

The concrete SAML 2.0 assertion token layer of the Cohesion IdentityModel family. It takes the
neutral token base (`…Token`) to assertion-grade fidelity, normalized onto the root canonical
model:

- **Typed SAML structure** — `SamlNameId`, `SamlConditions` (with the AND-across audience rule),
  `SamlSubjectConfirmation`/`SamlSubjectConfirmationData`, and `SamlEncryptedElement` markers,
  replacing the previous `object?` condition bag.
- **Normalization onto root** — the subject is lifted from the NameID through the pinned recipe
  (`SamlSubjectExtensions.GetSubjectIdentifier`), attributes become claims keyed by their raw SAML
  name with SAML provenance, the authentication statement flows to the root
  `AuthenticationContext`, and the conditions window projects onto the base temporal members.
- **Validation** — `SamlToken.Validate(SamlTokenValidationOptions)`: the composed neutral
  issuer/temporal rules, the SAML audience-restriction rule (AND across, via
  `SamlConditions.IsAudienceSatisfied` — not the base's flat union), and the bearer
  subject-confirmation-data window (freshness + recipient/in-response-to when expected).
- **Constants** — `SamlConfirmationMethods` (bearer) and `SamlTokenValidationCodes`; NameID
  formats reuse the root `SubjectIdentifierFormats`.

## Status

Delivered by feature `[L01.01.12.08]` (#612). Completes the family-wide `object?`-purge — after
it, `IdentityClaimValue` is the single value currency across every IdentityModel surface.

## Dependencies

- `Assimalign.Cohesion.IdentityModel.Token` (and transitively the root canonical model). BCL
  otherwise; no `Microsoft.Extensions.*`. It does **not** reference the SAML protocol branch
  (`…Protocols.Saml`); the branches are independent, and the NameID recipe + bearer method are
  re-minted mirrors pinned equal by a root-tests drift guard.

## Scope

Assertion-token fidelity and token-substrate validation. It does **not** read or write SAML XML,
verify the assertion signature (a seam via the preserved `AssertionXml`), or decrypt encrypted
elements (preserved as `SamlEncryptedElement` markers) — those parse/keyed operations are deferred
seams. The full SAML Core / Web Browser SSO profile validation is the protocol branch's
`SamlAssertion.Validate`. See the family
[DESIGN.md](../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md) — "SAML token package
decisions" — and this project's [DESIGN.md](DESIGN.md) for the boundary and compatibility matrix.
