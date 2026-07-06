# Assimalign.Cohesion.IdentityModel.Token

> Assembly reference. Public API surface of the protocol-neutral token normalization layer.

Assimalign.Cohesion.IdentityModel.Token is the neutral token normalization layer between the root canonical identity contracts and the concrete token packages (JWT, SAML). It defines the format-independent token shape — `IIdentityToken` with its normalized subject, claim, temporal, and authentication-context surfaces — plus the immutable `IdentityToken` base that concrete token types derive from and populate through an `IdentityTokenDescriptor`. It also carries the protocol-neutral data validation currency (issuer match, audience membership, primary temporal window), returning findings as values rather than exceptions.

## Public types

### Token model

| Type | Role |
| --- | --- |
| `IIdentityToken` | Data-only contract for a token normalized onto the canonical model, independent of wire format. |
| `IdentityToken` | Abstract immutable base for token formats; snapshots a descriptor and validates protocol-neutral data rules. |
| `IdentityTokenDescriptor` | Mutable builder describing normalized token contents before materialization into an `IdentityToken` derivative. |
| `IdentityTokenKind` | Enum of token document formats (Unknown, JsonWebToken, Saml); fail-closed Unknown default. |

### Validation

| Type | Role |
| --- | --- |
| `IdentityTokenValidationOptions` | Expectations for data validation: required validation instant, clock skew, expected issuer and audience. |
| `TokenValidationResult` | Immutable validation outcome; computed `Succeeded`, ordered `Diagnostics` and `Errors`, shared `Success` instance. |
| `TokenValidationDiagnostic` | One normalized validation finding: severity, machine-readable code, message, and optional token member. |
| `TokenValidationSeverity` | Enum of finding severities (Error, Warning, Information); Error is the fail-closed zero value. |
| `TokenValidationCodes` | String constants for the protocol-neutral diagnostic codes (`issuer_mismatch`, `expired`, and peers). |

### Extensions

| Type | Role |
| --- | --- |
| `IdentityTokenExtensions` | `extension(IIdentityToken)` conveniences: `HasAudience`, `IsActive`, and `IsExpired` checks. |

## Usage

Validate a materialized token's protocol-neutral data rules:

```csharp
IdentityToken token = /* materialized by a concrete token package (JWT, SAML) */;

var result = token.Validate(new IdentityTokenValidationOptions(DateTimeOffset.UtcNow)
{
    ExpectedIssuer = "https://issuer.example.com",
    ExpectedAudience = "https://api.example.com",
});

if (!result.Succeeded)
{
    foreach (var diagnostic in result.Errors)
    {
        Console.WriteLine(diagnostic); // [Error] issuer_mismatch: ...
    }
}
```

Read normalized token data through the extension conveniences and the root claim vocabulary:

```csharp
IIdentityToken token = /* any normalized token */;

if (token.HasAudience("https://api.example.com") &&
    token.IsActive(DateTimeOffset.UtcNow, clockSkew: TimeSpan.FromMinutes(5)))
{
    var subject = token.Subject?.Value;
    var claims = token.Claims; // root IIdentityClaimCollection lookup vocabulary
}
```

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
