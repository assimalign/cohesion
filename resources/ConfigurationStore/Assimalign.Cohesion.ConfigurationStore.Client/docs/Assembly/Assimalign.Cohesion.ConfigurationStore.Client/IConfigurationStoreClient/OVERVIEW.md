# IConfigurationStoreClient

`IConfigurationStoreClient` is the asynchronous contract for reading configuration namespaces and
submitting commands to a ConfigurationStore endpoint.

## API

- `GetNamespaceAsync(string name, CancellationToken)` returns a direct JSON object's string or null
  values from `GET /cohesion/v1/namespaces?name=...`.
- `SendCommandAsync(ResourceCommand command, CancellationToken)` submits the command with
  `POST /cohesion/v1/commands`.

Both methods propagate cancellation. A blank namespace name raises `ArgumentException`; transport
and non-success status failures raise `HttpRequestException`. Invalid, empty, or JSON `null`
namespace documents raise `JsonException`. A null command raises `ArgumentNullException`.

## Usage

```csharp
IConfigurationStoreClient client = ConfigurationStoreClient.Create(endpoint, credential);

IReadOnlyDictionary<string, string?> values =
    await client.GetNamespaceAsync("apps/api", cancellationToken);
```

Create instances through `ConfigurationStoreClient`; the HTTP implementation is internal.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project overview](../../../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
