# ResourceCommand

`ResourceCommand` is the immutable command envelope accepted by
`ISecretStoreClient.SendCommandAsync`.

## API

```csharp
var command = new ResourceCommand(
    id: "command-1",
    kind: "secretstore.add-secret",
    owner: "appa",
    key: "apps/api/password",
    payload: payloadBytes);

await client.SendCommandAsync(command, cancellationToken);
```

`Id` is the idempotency identifier carried in the request body. `Kind`, `Owner`, and `Key` carry the
area-defined command identity, while `Payload` carries its bytes. The constructor raises
`ArgumentException` when `id`, `kind`, `owner`, or `key` is null, empty, or whitespace. An empty
payload is permitted.

The command transport serializes the envelope with source-generated, camel-case JSON metadata;
`Payload` appears as a base64 string.

## Links

- [Assembly overview](../OVERVIEW.md)
- [ISecretStoreClient](../ISecretStoreClient/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
