# Assimalign.Cohesion.Web.Authentication.Bearer

The JWT bearer authentication scheme handler for the Cohesion web stack.
Reads an `Authorization: Bearer` token, verifies its signature and
validates its claims, and materializes a `ClaimsPrincipal`. Stateless: it
re-validates the caller-supplied token on every request.

## What it provides

- `JwtBearerOptions` &mdash; valid issuers / audiences, signing keys,
  allowed algorithms, clock skew, and the name/role claim types. Issuer and
  audience validation are on by default: registering a scheme without at
  least one issuer and one audience fails unless `ValidateIssuer` or
  `ValidateAudience` is set to `false` explicitly.
- `JwtBearerDefaults` &mdash; the default scheme name (`"Bearer"`).
- `IJwtSignatureVerifier` + the public sealed `HmacJwtSignatureVerifier`,
  `RsaJwtSignatureVerifier` and `EcdsaJwtSignatureVerifier`, each with `Create`
  &mdash; the compatibility seam: HMAC remains Web-local, while the RSA/ECDSA factories
  adapt the reusable IdentityModel JWT verifiers.
- `JwtBearerHandler.Create(options)` &mdash; the factory the
  composition root calls; it returns the public sealed handler with private construction.

## How it fits

- Implements `IAuthenticationHandler` from
  `Assimalign.Cohesion.Web.Authentication` (the scheme model).
- Consumes `Assimalign.Cohesion.IdentityModel.Token.JsonWebToken` for
  document validation (issuer / audience / lifetime / algorithm) and asymmetric signature
  verification. The public Bearer seam remains stable, and headless callers can use the lower
  JWT verifier without referencing the Web area.
- Emits RFC 6750 `WWW-Authenticate: Bearer` challenges.

Register it at the composition root, not here:

```csharp
builder.Services.AddAuthentication(auth =>
{
    auth.Options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
    auth.AddJwtBearer(o =>
    {
        o.ValidIssuers.Add("https://issuer.example");
        o.ValidAudiences.Add("api://default");
        o.SigningKeys.Add(RsaJwtSignatureVerifier.Create(publicKey));
    });
});

app.UseAuthentication();
```

See [docs/DESIGN.md](docs/DESIGN.md) for the validation order, the
fail-closed defaults, the algorithm-confusion defense, and the
JWT&rarr;`ClaimsPrincipal` mapping.
