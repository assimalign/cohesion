# Assimalign.Cohesion.Web.Authentication.Bearer

The JWT bearer authentication scheme handler for the Cohesion web stack.
Reads an `Authorization: Bearer` token, verifies its signature and
validates its claims, and materializes a `ClaimsPrincipal`. Stateless: it
re-validates the caller-supplied token on every request.

## What it provides

- `JwtBearerOptions` &mdash; valid issuers / audiences, signing keys,
  allowed algorithms, clock skew, and the name/role claim types.
- `JwtBearerDefaults` &mdash; the default scheme name (`"Bearer"`).
- `IJwtSignatureVerifier` + `JwtSignatureVerifier.CreateHmac/CreateRsa/CreateEcdsa`
  &mdash; the compatibility seam: HMAC remains Web-local, while the RSA/ECDSA factories
  adapt the reusable IdentityModel JWT verifiers.
- `JwtBearerAuthentication.CreateHandler(options)` &mdash; the factory the
  composition root calls; the concrete handler stays internal.

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
builder.AddAuthentication(o => o.DefaultScheme = JwtBearerDefaults.AuthenticationScheme)
       .AddJwtBearer(o =>
       {
           o.ValidIssuers.Add("https://issuer.example");
           o.ValidAudiences.Add("api://default");
           o.SigningKeys.Add(JwtSignatureVerifier.CreateRsa(publicKey));
       });

app.UseAuthentication();
```

See [docs/DESIGN.md](docs/DESIGN.md) for the validation order, the
algorithm-confusion defense, and the JWT&rarr;`ClaimsPrincipal` mapping.
