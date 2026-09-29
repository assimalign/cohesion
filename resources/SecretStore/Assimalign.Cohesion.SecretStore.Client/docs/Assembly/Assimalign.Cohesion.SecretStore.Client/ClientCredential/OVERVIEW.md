# ClientCredential

`ClientCredential` carries the opaque Bearer token attached to every SecretStore request.

## API

```csharp
var credential = new ClientCredential(bootstrapToken);
```

The constructor rejects a null, empty, or whitespace token with `ArgumentException`. The client
does not parse, validate, refresh, or persist the token. The token is not exposed by the public
surface, and `ToString()` always redacts it.

Credentials are bound when `SecretStoreClient.Create` constructs a client. Item 25 can create a
new lightweight client for each reconciled credential while the underlying transport remains shared.

## Links

- [Assembly overview](../OVERVIEW.md)
- [SecretStoreClient](../SecretStoreClient/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
