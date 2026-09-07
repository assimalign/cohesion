# SecretStoreClient

`SecretStoreClient` is the factory for the package's internal HTTP protocol client.

## Factory

```csharp
ISecretStoreClient client = SecretStoreClient.Create(
    new EndpointAddress("https", "secrets.internal", 8443, "/api"),
    new ClientCredential(bootstrapToken));
```

`Create` accepts an HTTP or HTTPS `EndpointAddress` and a non-null `ClientCredential`. It performs
no network I/O. A non-HTTP scheme raises `ArgumentException`; a null credential raises
`ArgumentNullException`.

The returned client uses a process-shared BCL `HttpMessageInvoker`. Redirects and cookies are
disabled so its Bearer credential is not forwarded to another authority or mixed with ambient
cookie state. Callers do not own or dispose the shared transport.

## Links

- [Assembly overview](../OVERVIEW.md)
- [ISecretStoreClient](../ISecretStoreClient/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
