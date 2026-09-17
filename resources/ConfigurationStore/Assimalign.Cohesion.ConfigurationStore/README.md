# Assimalign.Cohesion.ConfigurationStore

Public, implementation-free contracts for composing a ConfigurationStore executable.

```csharp
IConfigurationStoreApplicationBuilder builder =
    ConfigurationStoreApplication.CreateBuilder(args);

builder.AddNamespace("app", ns => ns
    .Set("Mode", "production")
    .Set("Optional", null));

await using IConfigurationStoreApplication application = builder.Build();
await application.RunAsync();
```

Declarations are first-start seeds. The Hosting implementation preserves later command mutations
across restarts and does not overwrite an existing durable namespace document.

- [Overview](./docs/OVERVIEW.md)
- [Design](./docs/DESIGN.md)
