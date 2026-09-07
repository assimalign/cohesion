# Assimalign.Cohesion.SecretStore.Client

## Summary

This package is the gateway-side protocol boundary for reading secret bytes and PEM certificates
from a realized SecretStore resource. It keeps mount-source resolution out of runtime libraries
and hides its BCL HTTP implementation behind `ISecretStoreClient`.

## Public surface

- `ISecretStoreClient` reads secret bytes, reads PEM certificates, and sends a generic command.
- `SecretStoreClient.Create(Uri, ClientCredential)` creates the internal protocol
  implementation without performing network I/O.
- `ClientCredential` carries the opaque bootstrap token and redacts it when formatted.
- `ResourceCommand` is the generic command envelope; item 31c adds typed SecretStore commands.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore.Client;

var endpoint = new Uri("https://secretstore.internal:8443/api");
var credential = new ClientCredential(bootstrapToken);
ISecretStoreClient client = SecretStoreClient.Create(endpoint, credential);

ReadOnlyMemory<byte> value = await client.GetSecretAsync("apps/api/password", cancellationToken);
string certificate = await client.GetCertificateAsync("certs/appa-api", cancellationToken);
```

The factory accepts only HTTP or HTTPS endpoints. Each request presents the credential as a bearer
token; the package treats the token as opaque and does not acquire, parse, refresh, or persist it.
