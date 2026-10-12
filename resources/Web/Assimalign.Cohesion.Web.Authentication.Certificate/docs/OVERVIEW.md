# Assimalign.Cohesion.Web.Authentication.Certificate — Overview

Authenticates an HTTP request from the TLS client certificate accepted by its connection's
handshake policy. The default principal carries the certificate's subject, issuer, thumbprint and
serial number. Applications can map it to an account or reject it through an asynchronous callback.

## Scope and dependencies

This package supplies an `IAuthenticationHandler` and the C# 14 `AddCertificate` extensions on
`AuthenticationBuilder`. It depends on `Web.Authentication` for scheme dispatch and `Http.Tls` for
the connection certificate. It has no hosting or dependency-injection reference. `App.Web` delivers
it to `Sdk.Web` applications; standalone consumers reference the package directly.

The listener owns certificate requests, chain validation and trust. Configure its
`TlsServerOptions.AllowClientCertificate()` or `RequireClientCertificate()` before starting the
endpoint. Their default validator uses platform trust; a supplied validator defines application
trust. Registering the authentication scheme alone does not request a certificate.

## Usage

Inside a Web application's composition root, after configuring its HTTPS endpoint:

```csharp
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authentication.Certificate;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Routing;

builder.Services.AddRouting();
builder.Services.AddAuthentication(auth =>
{
    auth.Options.DefaultScheme = CertificateAuthenticationDefaults.AuthenticationScheme;
    auth.AddCertificate();
});
builder.Services.AddAuthorization();

var app = builder.Build();
var routes = app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
// Map protected routes with RequireAuthorization(), or a policy selecting "Certificate".
```

Use `auth.AddCertificate("Device", options => ...)` for a named scheme. Set
`options.OnCertificateValidated` to augment or replace `context.Principal`, or call
`context.Fail("Account disabled.")` to reject a certificate the TLS layer accepted. The callback
receives a cancellation token. The certificate is borrowed from the connection: do not dispose it.

A connection without a certificate is unauthenticated (`NoResult`). A protected endpoint challenges
with `401` and `WWW-Authenticate: Certificate`; an authenticated caller failing its policy gets
`403`. Other schemes' existing challenges and parameters are preserved. The challenge cannot
acquire a certificate after the handshake. An untrusted certificate fails the TLS handshake before
an HTTP response can be sent.

See [DESIGN.md](DESIGN.md) for the trust boundary, claim names, callback lifecycle and endpoint-mode
semantics, and the [Web area map](../../README.md) for pipeline composition.
