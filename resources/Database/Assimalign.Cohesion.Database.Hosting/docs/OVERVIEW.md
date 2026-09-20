# Assimalign.Cohesion.Database.Hosting

`DatabaseApplication.CreateBuilder(args)` composes a complete Database host through dependency-free model intent, one-shot Build, then ordinary engine access or the Hosting Run extensions. Engines are operational at Build; optional nested servers begin listening at Start.

```csharp
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;

var builder = DatabaseApplication.CreateBuilder(args);
builder.AddSql((_, engine) =>
{
    engine.EngineName = "orders";
    engine.RootPath = dataPath;
    engine.AddServer(value => SqlDatabaseServer.Create(
        (SqlDatabaseEngine)value, new SqlDatabaseServerOptions { Listener = listener }));
});
builder.AddDatabase("orders", "orders", SqlSchema.Compile("orders", database =>
    database.Table<Order>(table => table.Key(order => order.Id))));

await using var app = builder.Build();
var orders = app.Context.GetEngine("orders");
await app.RunAsync();
```

Model verbs are `AddSql`, `AddDocuments`, `AddGraph`, `AddKeyValue`, and `AddBlob`; they return the application builder and ship with their model package. Servers and workers are registered with deferred factories on the model engine builder. There is no application-level AddServer or Use composition method. Direct model `Engine.Create(options)` remains available for standalone use.

The concrete hosting builder supplies `Configuration`, `Services`, build-aware `AddEngine(name, factory)`, additional lifecycle services, health contributions and named compiled-schema provisioning. Services start before servers; servers drain first. The root interfaces expose no configuration or DI types. Runtime context includes all engines, their nested servers, configuration and services.

Instance engines and services remain caller-owned. Factory products belong to the application; engines own their nested servers/workers. Application disposal stops the host, disposes its owned services and engines, then its provider, configuration and configuration file system. Legacy options/direct construction remain borrowed-input paths. Each application supports one start lifecycle.

Configuration loads optional base/environment JSON, `COHESION_CONFIG__` environment variables, captured args and explicit providers. Services use the Cohesion interpreted resolver (`EnableDynamicCode = false`) on both JIT and NativeAOT, with scope validation and explicit closed factories/instances. See [DESIGN.md](DESIGN.md) for ordering, rollback, isolation and ownership details.

The runtime references the area root and non-area infrastructure, never Database model packages. Existing enabled-resource runner/admin/telemetry integration is preserved; this implementation adds no ApplicationModel functionality.
