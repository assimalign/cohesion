# Assimalign.Cohesion.Security

## Summary

Platform-agnostic X.509 certificate management for Cohesion. This iteration covers cross-platform
certificate **loading** (PKCS#12 / PFX and PEM); operating-system certificate **store** access is
out of scope and tracked separately.

## Dependencies

- None beyond the .NET base class library (`System.Security.Cryptography.X509Certificates`).

## Key Types

- `ICertificateManager` — loads certificates from PKCS#12 / PFX (file or bytes) and PEM (file).
- `CertificateManagerFactory.Create()` — returns the default platform-agnostic manager.
- `CertificateException` — the area-scoped exception root for load failures.

## Usage

```csharp
using Assimalign.Cohesion.Security;

ICertificateManager certificates = CertificateManagerFactory.Create();
var serverCertificate = certificates.LoadPkcs12FromFile("server.pfx", "p@ss");
// hand serverCertificate to TlsServerOptions.AuthenticationOptions.ServerCertificate
```

## Source Layout

- `src/Abstractions` — `ICertificateManager`.
- `src/Internal` — the default `CertificateManager` implementation.
- `src/` — `CertificateManagerFactory` and `CertificateException`.
