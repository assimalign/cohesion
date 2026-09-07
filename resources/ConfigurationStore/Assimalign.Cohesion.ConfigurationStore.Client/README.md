# Assimalign.Cohesion.ConfigurationStore.Client

Thin, AOT-compatible protocol client for reading configuration namespaces and submitting
configuration-store commands. The package is intended for gateways and other infrastructure that
must resolve configuration without referencing the ConfigurationStore hosting runtime.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore.Client;

IConfigurationStoreClient client = ConfigurationStoreClient.Create(
    new Uri("https://configuration.internal:8443"),
    new ClientCredential(bootstrapToken));

IReadOnlyDictionary<string, string?> values =
    await client.GetNamespaceAsync("apps/api", cancellationToken);
```

`ClientCredential` is opaque and is sent as a Bearer credential on each request. Its formatted
representation is always redacted. The default transport also disables redirects so credentials
are not forwarded to a different authority.

## Protocol

- `GET /cohesion/v1/namespaces?name=<escaped-name>` returns a direct JSON object whose property
  values are strings or `null`.
- `POST /cohesion/v1/commands` sends a camel-case JSON `ResourceCommand` envelope whose payload
  bytes are base64 encoded.

An endpoint base path is preserved and prepended to both routes. Non-success HTTP responses raise
`HttpRequestException`; malformed namespace JSON raises `JsonException`.

## Dependencies

The package references only `Assimalign.Cohesion.Core` for the `Uri` endpoint guard and uses the BCL HTTP
and source-generated JSON stacks. It does not reference ConfigurationStore.Hosting, dependency
injection, or a shared framework.

## Documentation

- [Overview](./docs/OVERVIEW.md)
- [Design](./docs/DESIGN.md)
- [Assembly reference](./docs/Assembly/Assimalign.Cohesion.ConfigurationStore.Client/OVERVIEW.md)
