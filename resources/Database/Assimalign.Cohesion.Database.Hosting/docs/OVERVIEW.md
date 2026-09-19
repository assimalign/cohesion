# Assimalign.Cohesion.Database.Hosting

## Summary

The standalone hosting application for the database resource: a `Host<TContext>`
subclass that composes the resource's units of work as hosted services on the
per-service execution menu, and the one DI/Configuration/Logging seam for the
area. Composition-only: it wraps the composed per-model wire-protocol servers
(`IDatabaseServer`) generically as endpoint host services — the server machinery
itself lives inside each model package (the SQL model's `SqlDatabaseServer` in
`Assimalign.Cohesion.Database.Sql`), and engines are self-sufficient data
machines this module never drives (see `docs/DESIGN.md`).

## Current Evaluation

- Status: Composition delivered on the redesigned shape (2026-07-13) — the
  application runs the composition root's services, then the registered servers
  (started last, drained first), and implements the root's application-builder
  seam. Declared databases are provisioned as services before accept. Engines take
  no part in the host lifecycle; workers are engine-internal and observed through
  the application context's health contribution.
- Project references: `Assimalign.Cohesion.Database` (area root) and
  `Assimalign.Cohesion.Hosting` (plain lifecycle),
  `Assimalign.Cohesion.Hosting.Health` (health contribution contracts), and
  `Assimalign.Cohesion.Hosting.Resources` (opt-in resource runtime/control plane), plus
  private cross-area references to `Web.Hosting`, `Web.Hosting.Resources`, `Web.Hosting.Health`, and `Web.Health` for the enabled
  resource's `admin` endpoint. No Database model package is referenced and no
  Database hosting-isolation exemption is used.

## Primary Responsibilities

- `DatabaseApplication` owns the resource process lifecycle (start, run, stop)
  via `Host<DatabaseApplicationContext>`, composing (in registration order) the
  composition root's additional host services, then one endpoint service per
  registered server — servers start last and drain first.
- `DatabaseApplicationContext` implements the root's
  `IDatabaseApplicationContext`: the registered servers (plural — one per model)
  and the server-less engine registrations. It also implements
  the `Hosting.Health` `IHealthContributor`, folding every distinct registered or server-fronted
  engine's `State` and `Workers` inventory into one Database contribution.
- `DatabaseApplicationOptions` collects the servers, the embedded engine
  registrations, and additional `IHostService`s.
- `DatabaseApplicationBuilder.AddService` registers a plain `IHostService`
  instance or a factory over the final concrete `DatabaseApplicationContext`; services
  retain registration order, start before all servers, and stop after them in
  reverse order.
- `DatabaseApplicationBuilder.Provision` and `AddDatabase` register code-first
  before-accept provisioning; `AddDatabase` receives the schema compiled by its
  model package and retains/returns that `CompiledSchema`. The model database applies only
  that compiled contract and records its content hash.
  Provisioning creates only after `OpenDatabaseAsync` reports
  `DatabaseNotFoundException`; other database failures propagate from startup.
- Enabled resources host their registered `Hosting.Resources` `IResourceControlPlane` on the ambient
  `admin` endpoint. Health routes use `Web.Health` with the `Web.Hosting.Health` contributor adapter; endpoint observation,
  graceful stop, and command dispatch use the shared Web control-plane middleware.

## Key Types

- `DatabaseApplication` (implements the root's `IDatabaseApplication`; `CreateBuilder()` is the composition entry point)
- `DatabaseApplicationBuilder` (implements the root's `IDatabaseApplicationBuilder`)
- `DatabaseApplicationContext` (implements the root's `IDatabaseApplicationContext`)
- `DatabaseApplicationOptions`

## Composing a host

Builder-first — model packages register their engines *and servers* through the
root's `IDatabaseApplicationBuilder` seam (verbs ship with the model package,
e.g. `AddSqlDatabase` / `AddSqlServer` in `Database.Sql`):

```csharp
var builder = DatabaseApplication.CreateBuilder();

SqlDatabaseEngine engine = builder.AddSqlDatabase(options => options.RootPath = dataPath);
SqlCompiledSchema schema = SqlSchema.Compile("orders", database =>
    database.Table<Order>(table => table.Key(order => order.Id)));
builder.AddDatabase(engine, "orders", schema);
SqlDatabaseServer server = builder.AddSqlServer(engine, options => options.Listener = listener);

await using var app = builder.Build();
await app.RunAsync();   // starts services, then the servers (the engine is already live)
```

Additional host services can be registered through the root seam with
`builder.AddService(service)` or its typed context-factory overload.
`builder.Options.Services` remains available for fully manual option composition,
and constructing `new DatabaseApplication(options)` directly from fully populated
options remains supported. A custom or embedded host creates a model server
(`SqlDatabaseServer.Create(engine, options)`) and drives
`IDatabaseServer.StartAsync`/`StopAsync` on its own lifecycle — or skips servers
entirely and uses the engine in-process. `Database.Client` is the counterpart on
the other end of the wire.

The `string[] args` builder overload is the enabled-resource entry point. It honors
`Hosting.Resources` `ResourceRuntime.Current` and an assembly-keyed generated control-plane
registration; the no-argument and options overloads stay plain hosts and bind no admin listener.


## Declarative commands

Enabled hosts register database.add-database and database.add-principal handlers when advertised by
their generated control plane. The database payload is UTF-8 JSON with database and optional engine;
the key is `engine/database` when an engine is supplied, otherwise the database name. Database names
cannot contain `/`; engine names may contain it, keeping the ownership identity unique.
An omitted engine requires exactly one registered or server-fronted
engine. Creation calls IDatabaseEngine.CreateDatabaseAsync and teardown calls DropDatabaseAsync,
with the shared control-plane ownership ledger gating both. Existing databases are refused rather
than adopted into a declaration that could later delete them.
