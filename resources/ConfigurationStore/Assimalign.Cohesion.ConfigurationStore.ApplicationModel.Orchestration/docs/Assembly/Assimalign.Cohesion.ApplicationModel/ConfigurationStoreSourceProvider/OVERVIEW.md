# ConfigurationStoreSourceProvider

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration`

## Purpose

`ConfigurationStoreSourceProvider` implements `IResourceSourceProvider` for ConfigurationStore
resources. The gateway calls it for every `<store>:<namespace>` Configuration mount whose source
name it is registered under in `ApplicationProviders.Sources`.

```csharp
builder.Providers.Sources["settings"] = new ConfigurationStoreSourceProvider();
// equivalent to builder.UseConfigurationStore(settings)
```

## Members

| Member | Behavior |
| --- | --- |
| `ConfigurationStoreSourceProvider()` | Creates a stateless provider; one instance may serve any number of stores and concurrent reads. |
| `ResourceKind` | `"ConfigurationStore"`: the source name must be a ConfigurationStore resource of the declaring application. |
| `ReadConfigurationAsync(request, cancellationToken)` | Sends `GET /cohesion/v1/namespaces?name=<key>` to the scheme, host, and port of `request.Store.ControlPlaneAddress` with `Authorization: Bearer <credential>`, over a new transport per read that validates the store's TLS certificate with `ServerCertificateValidator`. Returns the namespace entries, `null` values kept. |
| `ReadSecretAsync`, `ReadCertificateAsync` | Interface defaults: throw `NotSupportedException`. |

## Exceptions

| Condition | Exception |
| --- | --- |
| `request` is `null` | `ArgumentNullException` |
| `request.Kind` is not `Configuration` | `NotSupportedException` |
| `request.Store` is `null` or not a ConfigurationStore connection; the address is not an HTTP(S) endpoint; the credential or namespace is blank | `ArgumentException` |
| The store is unreachable or answers a non-success status (`404` for a namespace it does not hold) | `HttpRequestException` carrying `StatusCode` |
| The namespace document is malformed or JSON `null` | `JsonException` |
| The token is cancelled | `OperationCanceledException` |

## Links

- [Assembly overview](../OVERVIEW.md)
- [ConfigurationStoreOrchestrationExtensions](../ConfigurationStoreOrchestrationExtensions/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
