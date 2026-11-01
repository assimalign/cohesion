# Assimalign.Cohesion.SecretStore.Client

## Summary

This package is the protocol boundary for reading secret bytes and PEM certificates from a
realized SecretStore resource. A program that reads the store
references it directly; a gateway does not, because it reaches a managed instance through the
resource integration, `Assimalign.Cohesion.SecretStore.Hosting.ApplicationModel`. It keeps mount-source resolution out of runtime libraries and hides its
BCL HTTP implementation behind `ISecretStoreClient`.

## Public surface

- `ISecretStoreClient` reads secret bytes and PEM certificates.
- `SecretStoreClient.Create(Uri, ClientCredential)` creates the internal protocol
  implementation without performing network I/O.
- `ClientCredential` carries the opaque bootstrap token and redacts it when formatted.
- `SecretStoreCertificateReference` validates certificate retrieval references before HTTP.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore.Client;

var endpoint = new Uri("https://secretstore.internal:8443/api");
var credential = new ClientCredential(bootstrapToken);
ISecretStoreClient client = SecretStoreClient.Create(endpoint, credential);

ReadOnlyMemory<byte> value = await client.GetSecretAsync("apps/api/password", cancellationToken);
string certificate = await client.GetCertificateAsync("certs/appa-api", cancellationToken);
```

`GetCertificateAsync` accepts `SecretStoreCertificateReference`; string arguments convert
implicitly. Supported references are exact `ca/root`, or `certs/` plus a nonblank leaf name with
neither slash nor backslash and unequal to `.` or `..`. References preserve case and compare
ordinally. Use `SecretStoreCertificateReference.TryCreate` for untrusted input without exceptions.
Invalid references and `default` are refused before sending a request. The typed parameter replaces
the preview.1 string signature, so implementations and precompiled consumers must be updated.

The certificate response is a PEM bundle containing the persistent first-issued leaf, its PKCS#8
private key, and the issuer chain. A `parameter:` source bypasses this client and is mounted by the
gateway unchanged. `certs/public`/`Certificate="public"` is reserved for a later public-CA item.

The factory accepts only HTTP or HTTPS Cohesion endpoint URIs: absolute, host-bearing values with a
valid port and no user information, query, or fragment. Each request presents the credential as a
bearer token; the package treats the token as opaque and does not acquire, parse, refresh, or persist
it.

## Removed: the command surface (2026-11-01)

`SendCommandAsync`, `ObserveCommandAsync`, `DeleteCommandAsync`, `ResourceCommand`,
`ResourceCommandObservation`, and `CreateForControlPlane` targeted `/cohesion/v1/commands`, a route
of the old resource control plane that SecretStore.Hosting no longer serves. They were removed with
it; a managed instance takes state changes as actions through its platform (see [Design](./DESIGN.md)).
