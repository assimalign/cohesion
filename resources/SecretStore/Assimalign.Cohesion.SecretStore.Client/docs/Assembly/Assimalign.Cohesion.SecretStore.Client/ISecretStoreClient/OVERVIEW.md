# ISecretStoreClient

`ISecretStoreClient` is the asynchronous contract for reading secret material and submitting
commands to a SecretStore endpoint.

## API

- `GetSecretAsync(string path, CancellationToken)` returns the stored bytes from
  `GET /cohesion/v1/secrets?path=...`.
- `GetCertificateAsync(string name, CancellationToken)` returns PEM text from
  `GET /cohesion/v1/certificates?name=...`.
- `SendCommandAsync(ResourceCommand command, CancellationToken)` submits the command with
  `POST /cohesion/v1/commands`.

All methods propagate cancellation. Blank paths or names raise `ArgumentException`; transport and
non-success status failures raise `HttpRequestException`. An empty certificate raises
`InvalidDataException`, and command serialization may raise `JsonException`.

## Usage

```csharp
ISecretStoreClient client = SecretStoreClient.Create(endpoint, credential);

ReadOnlyMemory<byte> secret =
    await client.GetSecretAsync("apps/api/password", cancellationToken);
string certificate =
    await client.GetCertificateAsync("certs/appa-api", cancellationToken);
```

Create instances through `SecretStoreClient`; the HTTP implementation is internal.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project overview](../../../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
