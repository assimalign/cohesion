# IConfigurationNamespaceBuilder

Namespace: `Assimalign.Cohesion.ConfigurationStore`
Assembly: `Assimalign.Cohesion.ConfigurationStore`

## Purpose

`IConfigurationNamespaceBuilder` declares the first-start entries for one named namespace.

## API

```csharp
builder.AddNamespace("app", ns => ns
    .Set("Mode", "production")
    .Set("Optional", null));
```

`Set` accepts a nonblank key without `/` and a string or null value, replaces an earlier declaration
for the same key in the callback, and returns the builder for chaining. The wire protocol reserves
`/` to separate a namespace from its entry key. Declarations seed only missing durable namespaces;
they are not reapplied over values changed through the command endpoint.

## Links

- [Builder](../IConfigurationStoreApplicationBuilder/OVERVIEW.md)
- [Project design](../../../DESIGN.md)
