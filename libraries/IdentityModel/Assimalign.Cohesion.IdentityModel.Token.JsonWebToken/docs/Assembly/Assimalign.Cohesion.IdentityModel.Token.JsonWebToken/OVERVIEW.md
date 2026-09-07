# Assimalign.Cohesion.IdentityModel.Token.JsonWebToken

> Assembly reference. Public API surface of the JWT document layer: compact parsing and ES256 writing, the typed JOSE header, asymmetric signature verification, and document-level validation.

This assembly is the concrete JOSE/JWT document layer of the IdentityModel token branch. It parses compact JWS serialization onto the canonical identity token model, writes ES256 tokens from that model, exposes the typed JOSE header as computed projections of its raw parameters, and provides reusable RSA/ECDSA verification over the exact compact signing input. `JsonWebToken.Validate` remains a separate document-rule operation; it never invokes a verifier or resolves trust keys.

## Public types

### Token document

| Type | Role |
| --- | --- |
| `IJsonWebToken` | Normalized JWT contract: typed JOSE header, declared algorithm, compact parts, and signing-input seam. |
| `JsonWebToken` | Immutable materialized JWT; parses compact serialization and validates document-level rules against options. |
| `JsonWebTokenDescriptor` | Mutable pre-materialization shape: token claims plus typed JOSE header and optional compact parts. |
| `JsonWebTokenParts` | Compact serialization segments (header, payload, signature) and the exact-octets JWS signing input. |

### Writing and signature verification

| Type | Role |
| --- | --- |
| `IJsonWebTokenWriter` | Interface-first compact-JWS writer contract over `JsonWebTokenDescriptor`. |
| `JsonWebTokenWriter` | Creates an internal ES256 writer bound to a caller-owned NIST P-256 private key and non-empty `kid`. |
| `IJsonWebTokenSignatureVerifier` | Raw asymmetric signature seam: algorithm/key-id selection plus verification over decoded octets. |
| `JsonWebTokenSignatureVerifier` | Creates internal RSA (`RS*`/`PS*`) and named-curve ECDSA (`ES*`) verifiers. |

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

Write an ES256 bootstrap credential from the existing token descriptor model:

```csharp
using System.Security.Cryptography;

using Assimalign.Cohesion.IdentityModel.Token.JsonWebToken;

using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
IJsonWebTokenWriter writer = JsonWebTokenWriter.CreateEs256(signingKey, "bootstrap-key-1");
var descriptor = new JsonWebTokenDescriptor
{
    Issuer = "https://gateway.example",
    ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
    Id = Guid.NewGuid().ToString("N"),
};
descriptor.Audiences.Add("secret-store");

string compact = writer.Write(descriptor);
```

Verify the exact compact signing input separately from document validation:

```csharp
using System.Buffers.Text;
using System.Text;

if (JsonWebToken.TryParse(compact, out var token) && token?.Parts is { } parts)
{
    IJsonWebTokenSignatureVerifier verifier =
        JsonWebTokenSignatureVerifier.CreateEcdsa(verificationKey, "bootstrap-key-1");
    byte[] signingInput = Encoding.ASCII.GetBytes(parts.SigningInput);
    byte[] signature = Base64Url.DecodeFromChars(parts.Signature);
    bool authentic = verifier.CanVerify(token.Algorithm!, token.Header.KeyId) &&
        verifier.Verify(token.Algorithm!, signingInput, signature);
}
```

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [IdentityModel family keystone](../../../../Assimalign.Cohesion.IdentityModel/docs/DESIGN.md)
