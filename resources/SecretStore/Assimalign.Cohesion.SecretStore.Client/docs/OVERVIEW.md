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

The certificate response is a PEM bundle containing the persistent first-issued leaf, its PKCS#8
private key, and the issuer chain. A `parameter:` source bypasses this client and is mounted by the
gateway unchanged. `certs/public`/`Certificate="public"` is reserved for a later public-CA item.

The factory accepts only HTTP or HTTPS Cohesion endpoint URIs: absolute, host-bearing values with a
valid port and no user information, query, or fragment. Each request presents the credential as a
bearer token; the package treats the token as opaque and does not acquire, parse, refresh, or persist
it.

## Observed command delivery

CreateForControlPlane accepts the full control-plane prefix. ObserveCommandAsync and
DeleteCommandAsync return ResourceCommandObservation; SendCommandAsync retains its original
Task-returning behavior. The new secret/certificate commands return 200 application/octet-stream
on success and JSON {status,detail} on refusal. Legacy cohesion.trust.add keeps empty 204/409/403
responses. The observation client treats an empty 2xx body as Applied, or Deleted for DELETE,
and supplies a named HTTP detail when a legacy refusal has no body. DELETE is for the new kinds;
trust grants remain POST-only. The package still has exactly one Core reference and no Hosting
or Gateway dependencies. Identity verification and grant policy belong to the endpoint.
