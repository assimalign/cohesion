# Assimalign.Cohesion.IdentityModel

> Assembly reference. Public API surface of the canonical identity model and the cross-protocol claim canonicalization seam.

Assimalign.Cohesion.IdentityModel is the canonical, protocol-neutral identity model at the root of the IdentityModel family. It defines the normalized shapes — subjects, claims, typed values, credentials, authentication results, contexts, and sessions — that OpenID Connect, SAML 2.0, and other protocol branches normalize into, so consuming services see one identity surface regardless of wire protocol. It also carries the canonicalization seam (`IdentityClaimMappings`, `IdentityClaimMapper`, and the `Canonicalize` extensions) that re-types strictly equivalent wire claim names onto the canonical vocabulary while preserving values byte-identically and provenance losslessly. All model types are immutable and materialize from mutable descriptors.

## Public types

### Abstractions

| Type | Role |
| --- | --- |
| `IIdentityClaim` | Contract for a single normalized claim: canonical type, typed value, issuer, provenance. |
| `IIdentityClaimCollection` | Immutable, insertion-ordered claim list with Try/Get lookups and flattening multi-value accessors. |
| `IIdentitySubject` | Protocol-neutral authenticated principal: kind, identifiers, claims, and actor delegation chain. |

### Claims and values

| Type | Role |
| --- | --- |
| `IdentityClaim` | Immutable normalized claim; rejects undefined values at construction. |
| `IdentityClaimCollection` | Immutable snapshot implementation of the claim collection contract, with `Empty` singleton. |
| `IdentityClaimValue` | Typed, immutable value struct with structural equality, spanning string through nested array/object shapes. |
| `IdentityValueKind` | Enum of normalized value shapes, `Undefined` through `Object`; stable and additive-only. |
| `IdentityAttribute` | Immutable named multi-value attribute, the attribute-shaped counterpart to claims. |
| `IdentityClaimProvenance` | Records the original protocol, wire name, issuer, value type, and formats behind a normalized claim. |
| `IdentityClaimTypes` | Canonical claim-type vocabulary constants, adopting the IANA-registered JWT names. |

### Canonicalization seam

| Type | Role |
| --- | --- |
| `IdentityClaimMappings` | Default table of wire names (OID URNs, WS-Federation URIs) strictly equivalent to canonical types. |
| `IdentityClaimMapperDescriptor` | Mutable mapper configuration: custom mappings layered over the optional default table. |
| `IdentityClaimMapper` | Idempotent name-based canonicalizer re-typing mapped claims; values byte-identical, provenance preserved, structural targets forbidden. |

### Subjects

| Type | Role |
| --- | --- |
| `IdentitySubject` | Immutable canonical subject materialized from a descriptor; bounds actor chains at `MaxActorDepth`. |
| `IdentitySubjectDescriptor` | Mutable descriptor for building an `IdentitySubject`. |
| `SubjectIdentifier` | Protocol-neutral subject identifier; equality over value, format, issuer, and relying-party qualifier. |
| `SubjectIdentifierFormats` | Well-known identifier format constants: SAML NameID URIs, OIDC subject types, `client_id`. |
| `IdentityKind` | Enum of principal kinds: `Unknown`, `User`, `Application`. |

### Authentication

| Type | Role |
| --- | --- |
| `AuthenticationResult` | Immutable attempt outcome: exactly one of subject or failure, plus protocol and provenance. |
| `AuthenticationResultDescriptor` | Mutable descriptor for building an `AuthenticationResult`. |
| `AuthenticationFailure` | Failure-as-value: canonical code, message, and the original wire error code. |
| `AuthenticationFailureCodes` | Canonical failure-code constants that protocol wire errors map onto. |
| `AuthenticationProtocol` | Open, normalized protocol vocabulary struct (`oidc`, `oauth2`, `saml2`, `unknown`). |
| `AuthenticationContext` | How and when a subject authenticated: instant, context class, methods, session correlation. |
| `AuthenticationContextDescriptor` | Mutable descriptor for building an `AuthenticationContext`. |
| `AuthenticationSession` | Immutable sign-in session snapshot correlated to the provider session; `IsActive` computes liveness. |
| `AuthenticationSessionDescriptor` | Mutable descriptor for building an `AuthenticationSession`. |
| `AuthenticationSessionState` | Enum of administrative session states; `Unknown` default fails closed. |

### Credentials

| Type | Role |
| --- | --- |
| `IdentityCredential` | Immutable credential description — references and metadata only, never secret material; `IsUsable` computes validity. |
| `IdentityCredentialDescriptor` | Mutable descriptor for building an `IdentityCredential`. |
| `IdentityCredentialKind` | Enum of credential kinds, `Password` through `Passkey`; stable and additive-only. |
| `IdentityCredentialState` | Enum of administrative credential states; `Unknown` default fails closed. |

### Extensions and errors

| Type | Role |
| --- | --- |
| `IdentityClaimCollectionExtensions` | Extension accessors: `GetString`/`TryGetString`, `HasClaim`, and the `Canonicalize` entry points. |
| `IdentityModelException` | Area-scoped exception root for domain-invariant violations; protocol failures stay result values. |

## Usage

Canonicalize wire-named claims and read them through the canonical vocabulary:

```csharp
IIdentityClaimCollection claims = new IdentityClaimCollection(
[
    new IdentityClaim(
        "urn:oid:0.9.2342.19200300.100.1.3",
        "ada@example.com",
        issuer: "https://idp.example.com",
        provenance: new IdentityClaimProvenance(AuthenticationProtocol.Saml2)),
    new IdentityClaim(IdentityClaimTypes.Roles, IdentityClaimValue.FromArray(["reader", "editor"])),
]);

// Re-types the OID wire name to "email"; the wire name survives in provenance.
IIdentityClaimCollection canonical = claims.Canonicalize();

string? email = canonical.GetString(IdentityClaimTypes.Email);
bool isEditor = canonical.HasClaim(IdentityClaimTypes.Roles, "editor");
```

Materialize a subject and wrap it in an authentication result:

```csharp
var subject = new IdentitySubject(new IdentitySubjectDescriptor
{
    Kind = IdentityKind.User,
    Identifier = new SubjectIdentifier(
        "248289761001",
        SubjectIdentifierFormats.Public,
        issuer: "https://idp.example.com"),
    Claims = { new IdentityClaim(IdentityClaimTypes.Name, "Ada Lovelace") },
});

AuthenticationResult result = AuthenticationResult.Success(
    subject,
    AuthenticationProtocol.OpenIdConnect,
    DateTimeOffset.UtcNow);
```

## Links

- [Project overview](../../OVERVIEW.md)
- [Design (family keystone)](../../DESIGN.md)
