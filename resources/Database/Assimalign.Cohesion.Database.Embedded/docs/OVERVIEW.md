# Assimalign.Cohesion.Database.Embedded — Overview

The in-process consumption facade for Cohesion database engines: other resources (configuration stores, secret stores, schedulers, hubs) embed a data layer by composing model engines directly inside their process — no server, no wire protocol, no host — with the same engines and ACID guarantees as the hosted mode.

```csharp
await using var embedded = EmbeddedDatabase.Create(options =>
{
    options.Engines.Add(KeyValueDatabaseEngine.Create(new() { RootPath = dataPath }));
    options.Engines.Add(DocumentDatabaseEngine.Create(new() { RootPath = dataPath }));
});

embedded.TryGetEngine(EngineModel.Document, out var engine);
var database = await engine.OpenDatabaseAsync("settings");
```

## Scope

- `EmbeddedDatabase` — engine composition, lookup by name or model, reverse-order disposal (implemented, tested)
- `EmbeddedDatabaseOptions` — engine registration

## Dependencies

- `Assimalign.Cohesion.Database` (contract root) — and nothing else. Consumers add references to the model engines they embed.

## Consumers

Any Cohesion resource that needs a durable data layer without running a separate database service. The hosted counterpart is `Database.Hosting` (engines + wire-protocol server in a standalone process).
