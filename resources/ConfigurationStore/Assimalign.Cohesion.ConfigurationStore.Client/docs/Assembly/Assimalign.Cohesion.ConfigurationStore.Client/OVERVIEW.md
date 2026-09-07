# Assimalign.Cohesion.ConfigurationStore.Client

> Assembly reference for the ConfigurationStore namespace and command protocol client.

The assembly exposes an interface-first, HTTP-backed client for gateway-side ConfigurationStore
access. Its public types use the exact `Assimalign.Cohesion.ConfigurationStore.Client` namespace.

## Public types

| Type | Role |
| --- | --- |
| `IConfigurationStoreClient` | Reads a named namespace and submits resource commands asynchronously. |
| `ConfigurationStoreClient` | Creates clients bound to an HTTP or HTTPS endpoint `Uri`. |
| `ClientCredential` | Carries an opaque Bearer token and returns only a redacted formatted value. |
| `ResourceCommand` | Immutable command envelope containing id, kind, owner, key, and payload bytes. |

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore.Client;

IConfigurationStoreClient client = ConfigurationStoreClient.Create(
    new Uri("https://configuration.internal:8443/api"),
    new ClientCredential(bootstrapToken));

IReadOnlyDictionary<string, string?> values =
    await client.GetNamespaceAsync("apps/api", cancellationToken);
```

The namespace endpoint returns a direct JSON object. A JSON `null` value is preserved as a null
dictionary value; a null document or malformed JSON raises `JsonException`.

## Links

- [Project overview](../../OVERVIEW.md)
- [Project design](../../DESIGN.md)
- [Package README](../../../README.md)
