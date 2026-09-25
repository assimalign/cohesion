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
- `ObserveCommandAsync(ResourceCommand command, CancellationToken)` submits the same command and
  returns `ResourceCommandObservation` with `Status` and the provider's `Detail`, including refusals.
- `DeleteCommandAsync(ResourceCommand command, CancellationToken)` deletes the owned declaration
  with the same envelope and returns its observation. The explicit control-plane factory makes
  these two observation methods honor the manifest's exact path.

All methods propagate cancellation. A blank namespace name raises `ArgumentException`; transport
failures raise `HttpRequestException`. Non-success statuses throw for the original methods and
become `Rejected` observations for the observation methods. Invalid, empty, or JSON `null` namespace-list
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
