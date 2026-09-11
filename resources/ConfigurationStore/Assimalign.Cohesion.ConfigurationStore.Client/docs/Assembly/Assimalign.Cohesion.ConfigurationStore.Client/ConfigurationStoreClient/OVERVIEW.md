# ConfigurationStoreClient

`ConfigurationStoreClient` is the factory for the package's internal HTTP protocol client.

## Factory

```csharp
IConfigurationStoreClient client = ConfigurationStoreClient.Create(
    new Uri("https://configuration.internal:8443/api"),
    new ClientCredential(bootstrapToken));
```

`Create` accepts an HTTP or HTTPS endpoint `Uri` and a non-null `ClientCredential`. It performs
no network I/O. The URI must be absolute, contain a host and valid port, and have no user information,
query, or fragment. An invalid endpoint shape or non-HTTP scheme raises `ArgumentException`; a null
endpoint or credential raises `ArgumentNullException`.

The returned client uses a process-shared BCL `HttpMessageInvoker`. Redirects and cookies are
disabled so its Bearer credential is not forwarded to another authority or mixed with ambient
cookie state. Callers do not own or dispose the shared transport.

`CreateForControlPlane(Uri controlPlaneAddress, ClientCredential)` accepts the full manifest control-plane
URI, including custom paths. Its `ObserveCommandAsync` and `DeleteCommandAsync` append `/commands`
directly. The original factory and read/submission methods retain their existing route composition.

## Links

- [Assembly overview](../OVERVIEW.md)
- [IConfigurationStoreClient](../IConfigurationStoreClient/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
