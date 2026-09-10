# IConfigurationStoreClient

`IConfigurationStoreClient` is the asynchronous contract for listing and reading configuration
namespaces and submitting commands to a ConfigurationStore endpoint.

## API

- `ListNamespacesAsync(CancellationToken)` returns a direct JSON array's namespace-name strings from
  `GET /cohesion/v1/namespaces` with no query string.
- `GetNamespaceAsync(string name, CancellationToken)` returns a direct JSON object's string or null
  values from `GET /cohesion/v1/namespaces?name=...`.
- `SendCommandAsync(ResourceCommand command, CancellationToken)` submits the command with
  `POST /cohesion/v1/commands`.

All methods propagate cancellation. A blank namespace name raises `ArgumentException`; transport and
non-success status failures raise `HttpRequestException`. Invalid, empty, or JSON `null` namespace-list
and namespace-value documents raise `JsonException`. A null command raises `ArgumentNullException`.

## Usage

```csharp
IConfigurationStoreClient client = ConfigurationStoreClient.Create(endpoint, credential);

IReadOnlyList<string> namespaces =
    await client.ListNamespacesAsync(cancellationToken);

IReadOnlyDictionary<string, string?> values =
    await client.GetNamespaceAsync("apps/api", cancellationToken);
```

Create instances through `ConfigurationStoreClient`; the HTTP implementation is internal.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project overview](../../../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
