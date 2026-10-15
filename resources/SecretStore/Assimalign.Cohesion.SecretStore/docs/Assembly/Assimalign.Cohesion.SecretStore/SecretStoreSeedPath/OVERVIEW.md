# SecretStoreSeedPath

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose and validation

`SecretStoreSeedPath` is the generated value object accepted by
`ISecretStoreApplicationBuilder.AddSecret`. A valid seed path is nonblank and differs from exact
`trusted-issuers.json`, which belongs to the trust-grant protocol. Logical seed paths are not
file-system paths; separators, the `certs/` prefix, and surrounding spaces are preserved.

The constructor and implicit conversion from `string` throw `ArgumentException` for invalid
input. `IsValid` reports a rejection reason; `TryCreate` returns false and a default value without
throwing. The builder refuses `default`, which contains no path.

## Value behavior

`Value` exposes the original text. Equality, hashing, and ordering use ordinal comparison to
match the store's exact logical-path identity. Generated JSON conversion supports scalar values
and dictionary keys; JSON null reads as default and remains invalid at the builder boundary.

```csharp
SecretStoreSeedPath path = "apps/api/client-secret";
builder.AddSecret(path, secretBytes);
```
