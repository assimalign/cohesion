# Assimalign.Cohesion.IdentityModel.Token.JsonWebToken

> Assembly reference. Public API surface of the JWT document layer: compact-serialization parsing, the typed JOSE header, and document-level validation.

This assembly is the concrete JOSE/JWT document layer of the IdentityModel token branch. It parses compact JWS serialization onto the canonical identity token model, exposes the typed JOSE header as computed projections of its raw parameters, and validates document-level rules: algorithm posture, critical headers, required claims, and the keyless `at_hash`/`c_hash` value comparison. It does not verify signatures; the JWS signing input and encoded segments are exposed as the seam for a Security-layer verifier.

## Public types

### Token document

| Type | Role |
| --- | --- |
| `IJsonWebToken` | Normalized JWT contract: typed JOSE header, declared algorithm, compact parts, and signing-input seam. |
| `JsonWebToken` | Immutable materialized JWT; parses compact serialization and validates document-level rules against options. |
| `JsonWebTokenDescriptor` | Mutable pre-materialization shape: token claims plus typed JOSE header and optional compact parts. |
| `JsonWebTokenParts` | Compact serialization segments (header, payload, signature) and the exact-octets JWS signing input. |

### JOSE header

| Type | Role |
| --- | --- |
| `JoseHeader` | Immutable RFC 7515 header; typed accessors (`alg`, `typ`, `kid`, `crit`, `b64`) projected from raw parameters. |
| `JoseHeaderDescriptor` | Mutable header parameter bag materialized into a `JoseHeader`. |
| `JoseHeaderParameterNames` | JOSE header parameter name constants (RFC 7515 §4.1 plus `b64` from RFC 7797). |
| `JoseAlgorithms` | JOSE signature algorithm identifier constants (RFC 7518 §3.1), including the unsecured `none`. |

### Claims vocabulary

| Type | Role |
| --- | --- |
| `JsonWebTokenClaimTypes` | IANA JWT claim names beyond the registered core: `auth_time`, `nonce`, `acr`, `amr`, `azp`, `at_hash`, `c_hash`, `sid`. |

### Validation

| Type | Role |
| --- | --- |
| `JsonWebTokenValidationOptions` | Caller expectations: instant, clock skew, issuer/audience, allowed algorithms, required claims, hash inputs, known critical headers. |
| `JsonWebTokenValidationCodes` | Diagnostic codes minted by the JOSE/JWT document layer (`algorithm_none`, `at_hash_mismatch`, ...). |

## Usage

Parse a compact token and validate its document-level rules:

```csharp
using Assimalign.Cohesion.IdentityModel.Token;
using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

var token = JsonWebToken.Parse(compact);

TokenValidationResult result = token.Validate(new JsonWebTokenValidationOptions(DateTimeOffset.UtcNow)
{
    ExpectedIssuer = "https://issuer.example.com",
    ExpectedAudience = "my-client",
    AllowedAlgorithms = { JoseAlgorithms.RS256 },
    AccessToken = accessToken, // enables the keyless at_hash comparison
});

if (!result.Succeeded)
{
    foreach (var error in result.Errors)
    {
        Console.WriteLine($"{error.Code}: {error.Message}");
    }
}
```

Hand the signature material to a verifier (`Validate` never verifies the signature):

```csharp
if (JsonWebToken.TryParse(compact, out var token) && token?.Parts is { } parts)
{
    string signingInput = parts.SigningInput; // "header.payload", encoded segments as received
    string signature = parts.Signature;       // base64url-encoded signature segment
    string? keyId = token.Header.KeyId;       // key selection hint for the verifier
}
```

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
