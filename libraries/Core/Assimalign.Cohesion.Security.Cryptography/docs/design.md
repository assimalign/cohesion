# Assimalign.Cohesion.Security.Cryptography Design

## Design Intent

The package centers on certificate-store operations rather than generic cryptography primitives. A manager chooses the correct provider for the current platform, and providers expose the certificate lifecycle operations.

## Implementation Note

Examples below document the intended public shape; some members still throw NotImplementedException today.

## Architecture

- CertificateManager is the public entry point that selects providers based on OS and store location.
- ICertificateProvider encapsulates store access, certificate lookup, import, export, and creation.
- ICertificateResult gives callers a normalized view of certificate validity and trust state.

## Layout Example

```text
Assimalign.Cohesion.Security.Cryptography/
  src/
    Assimalign.Cohesion.Security.Cryptography.csproj
    Abstractions/
    Exceptions/
    Extensions/
    Internal/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Open a certificate provider for the current user store

```csharp
ICertificateManager manager = CertificateManager.Create(options =>
{
});

ICertificateProvider provider = manager.GetUserCertificateProvider("My");
ICertificateResult certificate = provider.GetCertificate("THUMBPRINT");
```

## Example 2: Create a self-signed certificate

```csharp
ICertificateResult result = provider.CreateSelfSignedCertificate(
    "CN=localhost",
    "localhost",
    "1.3.6.1.5.5.7.3.1",
    "Server Authentication");
```
