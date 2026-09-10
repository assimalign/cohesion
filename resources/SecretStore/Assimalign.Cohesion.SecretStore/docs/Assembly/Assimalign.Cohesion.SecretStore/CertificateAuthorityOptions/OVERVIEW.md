# CertificateAuthorityOptions

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose

`CertificateAuthorityOptions` is the dependency-free declaration captured by
`ISecretStoreApplicationBuilder.AddCertificateAuthority`. It describes how an empty durable store
obtains its first certificate authority; it does not perform certificate or network operations.

## Properties

- `CommonName` sets a standalone root-certificate subject common name. It defaults to
  `Cohesion SecretStore Certificate Authority`; an intermediate CSR instead uses the ambient
  application/resource identity.
- `SelfSeedWhenNoPlatform` defaults to `true`. It allows a self-signed development root only when
  neither initial material nor a Platform enrollment endpoint is configured.
- `PlatformEnrollmentEndpoint` optionally identifies the absolute HTTPS Platform SecretStore
  endpoint and selects pending gateway-mediated enrollment.
- `PlatformCertificate` optionally supplies the PEM-encoded Platform trust anchor/certificate pin.
- `InitialCertificate` and `InitialPrivateKey` supply paired PEM certificate and PKCS#8 private-key
  material, such as material resolved from deployment parameters.

## Selection and validation

Existing durable authority state takes precedence. On first start, paired initial material wins,
then pending Platform enrollment, then standalone self-seeding. Initial certificate and key values must be
supplied together; any configured material must be non-empty; the endpoint must be absolute; and a
configuration that disables self-seeding without selecting another source is invalid.

A failed configured enrollment does not fall back to self-seeding, because that would silently
create a second trust hierarchy.

## Usage

```csharp
builder.AddCertificateAuthority(options =>
{
    options.CommonName = "AppA Development Root CA";
    options.PlatformEnrollmentEndpoint = platformSecretStore;
    options.PlatformCertificate = platformCertificatePem;
});
```
