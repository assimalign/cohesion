# Assimalign.Cohesion.IdentityModel.Token.Saml

> Assembly reference. Public API surface of the SAML assertion token layer.

The assembly materializes SAML 2.0 assertions as immutable identity tokens normalized onto the canonical Cohesion identity model: the subject is lifted from the NameID through the pinned recipe, attributes become claims with SAML provenance, and the conditions window projects onto the base temporal members, while the typed SAML structure (NameID, conditions, subject confirmations, encrypted markers) is preserved with wire fidelity. It validates the token substrate — the neutral issuer and temporal rules, the AND-across / OR-within audience-restriction rule, and the bearer subject-confirmation-data window — as diagnostic values rather than exceptions. It does not read or write SAML XML, verify assertion signatures, or decrypt encrypted elements; the verbatim assertion XML and encrypted-element markers are preserved for those deferred keyed seams.

## Public types

### Token surface

| Type | Role |
| --- | --- |
| `ISamlToken` | Contract for a normalized SAML assertion token with the typed assertion structure. |
| `SamlToken` | Immutable SAML token; snapshots a descriptor, derives the normalized base surface, validates the substrate. |
| `SamlTokenDescriptor` | Mutable authoring shape; the typed SAML structure materialization derives the base surface from. |

### Assertion structure

| Type | Role |
| --- | --- |
| `SamlNameId` | SAML 2.0 `NameID` with wire fidelity: value, format, both qualifiers, SP-provided identifier. |
| `SamlConditions` | `Conditions` element; the authoritative audience surface (AND-across / OR-within) and temporal window. |
| `SamlSubjectConfirmation` | A `SubjectConfirmation`: method URI, optional confirming NameID, constraining data, bearer check. |
| `SamlSubjectConfirmationData` | `SubjectConfirmationData` window: recipient, temporal bounds, `InResponseTo`, address, `KeyInfo` XML. |
| `SamlEncryptedElement` | Preserved marker for an encrypted element (`EncryptedID`/`EncryptedAttribute`) a keyed decryptor opens later. |

### Vocabulary and subject lifting

| Type | Role |
| --- | --- |
| `SamlConfirmationMethods` | Subject confirmation method URI constants; defines the bearer method the substrate validates. |
| `SamlSubjectExtensions` | Pinned recipe lifting a `SamlNameId` into the canonical `SubjectIdentifier`. |

### Validation

| Type | Role |
| --- | --- |
| `SamlTokenValidationOptions` | Expectations for substrate validation: instant, clock skew, issuer, audience, recipient, `InResponseTo`, bearer posture. |
| `SamlTokenValidationCodes` | SAML-token-minted diagnostic codes beyond the shared `TokenValidationCodes`. |

## Usage

Materialize a SAML token from a descriptor; the normalized base surface (subject, claims, temporal window, audiences) is derived from the typed structure:

```csharp
using System;
using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Token.Saml;

var issuedAt = DateTimeOffset.UtcNow;

var descriptor = new SamlTokenDescriptor
{
    AssertionId = "_8e8dc5f6",
    Version = "2.0",
    Issuer = "https://idp.example.com",
    IssuedAt = issuedAt,
    NameId = new SamlNameId("7d2d0a3e", format: SubjectIdentifierFormats.Persistent),
    Conditions = new SamlConditions(
        notOnOrAfter: issuedAt.AddMinutes(5),
        audienceRestrictions: new[] { new[] { "https://sp.example.com" } }),
};

descriptor.SubjectConfirmations.Add(new SamlSubjectConfirmation(
    SamlConfirmationMethods.Bearer,
    data: new SamlSubjectConfirmationData(
        recipient: "https://sp.example.com/acs",
        notOnOrAfter: issuedAt.AddMinutes(5))));

descriptor.Attributes.Add(new IdentityAttribute(
    "urn:oid:2.5.4.42",
    new IdentityClaimValue[] { "Ada" },
    friendlyName: "givenName"));

var token = new SamlToken(descriptor);
```

Validate the token substrate against the relying party's expectations; findings come back as diagnostics, not exceptions:

```csharp
var result = token.Validate(new SamlTokenValidationOptions(DateTimeOffset.UtcNow)
{
    ExpectedIssuer = "https://idp.example.com",
    ExpectedAudience = "https://sp.example.com",
    ExpectedRecipient = "https://sp.example.com/acs",
    ExpectedInResponseTo = "_authn-request-42",
});

if (!result.Succeeded)
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"{error.Code}: {error.Message}");
    }
}
```

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
