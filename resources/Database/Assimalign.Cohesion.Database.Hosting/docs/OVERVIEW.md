# Assimalign.Cohesion.Database.Hosting

`DatabaseApplication.CreateBuilder(args)` composes a complete Database host through dependency-free model intent, one-shot Build, then ordinary engine access or the Hosting Run extensions. Engines are operational at Build; optional nested servers begin listening at Start.

```csharp
using Assimalign.Cohesion.Database;                      // GetEngine<TEngine>
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Hosting;

var builder = DatabaseApplication.CreateBuilder(args);
builder.AddSql("orders-sql", sql =>                        // the engine name, written once
{
    sql.Options.RootPath = builder.Configuration["Orders:DataPath"] ?? "data";
    sql.AddDatabase("orders", database => database.Schema(schema =>
        schema.Table<Order>("orders", table => table.Key(order => order.Id))));
    sql.AddServer(server => server.Listen(new Uri("cohesion-db://localhost:5740")));
});

await using var app = builder.Build();                     // engines built; cheap DDL done, pending tables reserved
SqlDatabaseEngine orders = app.Context.GetEngine<SqlDatabaseEngine>("orders-sql");
await app.RunAsync();                                      // services start, then servers accept
```

Model verbs (`AddSql`, `AddDocuments`, `AddGraph`, `AddKeyValue`, `AddBlob`) take an engine name
and a callback over its engine builder. Names are reserved before factories run. Model builders
declare servers, workers and databases; Build opens or creates the databases before servers accept.
SQL runs cheap schema steps there and defers populated-table work to its own provisioning worker
(49a), reserving affected tables before returning. Health reports Degraded with the current step
and row progress through the root instance's model-neutral observation. A deferred failure takes
that database offline; an inline failure disposes the already-built owned products.

`Build(CancellationToken)` passes its caller's shutdown token into SQL construction and recovery.
There is no construction timeout (`StartupTimeout` bounds service start only), and the host's
runtime shutdown lifecycle begins after Build. There is no application-level AddServer, AddDatabase
or Use composition method. Direct model `Create(name, options)` remains available for standalone use.

The concrete hosting builder supplies the host-level pieces `WebApplicationBuilder` does, as concrete types: `Environment` (`HostEnvironment`), `Configuration` (`ConfigurationManager`), `Logging` (`LoggerFactoryBuilder`) and `Services` (`ServiceProviderBuilder`); a build-aware `AddEngine(name, factory)` whose `DatabaseApplicationBuildContext` carries their built counterparts (the environment, the configuration, the `ServiceProvider` and the `LoggerFactory`); additional lifecycle services and health contributions. The application's own health contribution is unhealthy while any engine reports an offline database (`DatabaseEngine.OfflineDatabases`: a failed durable flush, #1243, drain of a journal's append buffer, #1252, or file header write, #1268, or an engine that gave up on a database after a background worker kept failing on it, owner decision 25), naming each with its `StorageOfflineCause` and its reopen attempts, until it is open again, degraded while an engine reports a worker that keeps failing (the description names each failing worker and the type of its last failure, never its message, since the health endpoint is unauthenticated and a message can carry file paths; the data carries `engine.N.worker.M.fault`, `.consecutiveFailures` and `.failureCount`, #1268), and unhealthy for a disposed engine. Services start before servers; servers drain first. The root's kept seams (`IDatabaseApplicationBuilder`, `IDatabaseApplicationContext`, `IDatabaseApplication`) name the root bases (`DatabaseEngine`, `DatabaseServer`) and expose no configuration or DI types. Runtime context includes all engines, their nested servers, configuration and services.

While the application runs it reopens an offline database by itself (owner decision 22): through the engine's own `OpenDatabaseAsync`, with exponential backoff and jitter from `Options.ReopenInitialDelay` (one second) up to `Options.ReopenMaximumDelay` (one minute), until the reopen succeeds or the application stops, side by side (one hung reopen holds back no other database), keeping the backoff of a database that goes offline again soon after its reopen (within its engine's `WorkerFailureWindow`, plus the longest interval among its workers that can take a database offline, plus `ReopenMaximumDelay`, 460 s at the defaults, and only while it is the instance the service reopened; owner decision 48 as its review revised it, pending the owner's acceptance), letting go of one reopened elsewhere, never reopening a database that was dropped, and never holding up Stop; `Options.ReopenOfflineDatabases = false` turns it off. Each finding, attempt and outcome is an event of the `Assimalign.Cohesion.Database.Hosting` event source, which an application's logging receives through `Assimalign.Cohesion.Logging.EventSource`.

Instance engines and services remain caller-owned. Factory products belong to the application; engines own their nested servers/workers. Application disposal stops the host, disposes its owned services and engines, then its provider, logger factory, configuration and configuration file system. `DatabaseApplicationOptions` carries host policy only (timeouts, the reopen policy); engines and services register on the builder. Each application supports one start lifecycle.

`CreateBuilder(args)` loads the default configuration when it creates the builder, as Web's does: optional base/environment JSON, `COHESION_CONFIG__` environment variables, the ambient resource settings and the arguments; providers added later win. `CreateBuilder()` and `new DatabaseApplicationBuilder(options)` start with an empty configuration. Unlike Web, there is no `CreateBuilder(options)` (owner decision 54): pass host settings with `new DatabaseApplicationBuilder(options)`. The environment and content root are read when the builder is created (from the ambient resource for `CreateBuilder(args)`, else `AppContext.BaseDirectory` for the files; from `Options` for the other overloads), and Build's options snapshot takes both from `Environment`. Build creates the one provider and logger factory: building `Services` or `Logging` directly yields a separate instance the application never uses, and building `Logging` makes the application's Build fail, as with Web's builder. Services use the Cohesion interpreted resolver (`EnableDynamicCode = false`) on both JIT and NativeAOT, with scope validation and explicit closed factories/instances; Build registers the environment, configuration and logger factory in the provider. See [DESIGN.md](DESIGN.md) for ordering, rollback, isolation and ownership details.

The runtime references the area root and non-area infrastructure, never Database model packages. Existing enabled-resource runner/admin/telemetry integration is preserved; this implementation adds no ApplicationModel functionality.
