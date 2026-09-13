# ApplicationModel

This Core-only package declares resources, dependency edges, external references, portable
realization plans, and application-owned resource commands. It supplies `IApplicationBuilder`,
`IApplicationModel`, application sets, and the transport-neutral gateway contracts.

```csharp
var builder = Application.CreateBuilder(ApplicationName.Parse("orders"), args)
    .UseGateway(gateway);
var database = builder.AddDatabase(databaseManifest);
database.AddDatabase("orders", engine: "sql");
var api = builder.AddWeb(apiManifest).DependsOn(database);
await builder.Build().RunAsync();
```

`AddDatabase`, `AddWeb`, and their typed descriptor command verbs come from their area's
ApplicationModel packages. A command is recorded as desired state. `Build()` validates its kind
against the target manifest and its owner and target against the declaring graph. The gateway
applies it after the target reaches Running and before admitting dependents.
Each target ownership key permits one desired command; competing values or set/remove
declarations are rejected. A subsequent model can replace the desired operation.

Custom area packages extend `IResourceCommandDescriptor` and pass their source-generated
`JsonTypeInfo<T>` to `AddCommand`. Explicit declarations use `ResourceCommands.Create` and
`IApplicationBuilder.AddCommand`. All implementations stay internal. A typed wrapper retains
its underlying resource reference; names alone never establish graph membership.

Built models expose immutable `Commands`. Portable model documents retain canonical command
payloads for application-set composition, so command payloads must never contain secrets.
Configuration-store verbs carry ordinary configuration values; secret commands are outside this slice.

See [DESIGN.md](DESIGN.md) for identity, validation, ownership, external references, lifecycle,
and NativeAOT boundaries; see the [package README](../README.md) for model and set composition.

## Trust grant command options

GatewayCommand carries an immutable AllowedCommandKinds list. Repeatable, comma-separated
--allow options are valid only in trust-add mode. Absent or empty grants mean unrestricted kinds;
trust-issue rejects the option. ApplicationModel parses and carries policy; the serving gateway
enforces it on apply and delete. The CLI's --against option remains deferred.
