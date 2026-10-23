# Assimalign.Cohesion.Database.Embedded — Overview

The in-process consumption facade for Cohesion database engines: other resources (configuration stores, secret stores, schedulers, hubs) embed a data layer by composing model engines directly inside their process — no server, no wire protocol, no host — with the same engines and ACID guarantees as the hosted mode.

```csharp
await using var embedded = EmbeddedDatabase.Create(options =>
{
    options.Engines.Add(KeyValueDatabaseEngine.Create("settings-kv", new() { RootPath = dataPath }));
    options.Engines.Add(DocumentDatabaseEngine.Create("settings-documents", new() { RootPath = dataPath }));
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

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Only stored fields and reviewed pure getters are projected; inspection does not execute queries, enumerate results, invoke clocks or mutate lifecycle state.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.
