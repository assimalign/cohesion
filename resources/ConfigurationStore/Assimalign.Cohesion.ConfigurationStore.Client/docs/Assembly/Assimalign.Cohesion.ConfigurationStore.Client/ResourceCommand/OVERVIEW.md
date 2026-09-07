# ResourceCommand

`ResourceCommand` is the immutable command envelope accepted by
`IConfigurationStoreClient.SendCommandAsync`.

## API

```csharp
var command = new ResourceCommand(
    id: "command-1",
    kind: "configurationstore.set-value",
    owner: "appa",
    key: "apps/api/mode",
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
- [IConfigurationStoreClient](../IConfigurationStoreClient/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
