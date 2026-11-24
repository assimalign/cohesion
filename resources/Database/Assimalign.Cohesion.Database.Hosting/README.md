# Assimalign.Cohesion.Database.Hosting

The Database area's runtime composition root. `DatabaseApplication` hosts the
registered database servers and additional host services.

When the builder resolves a health endpoint, the internal `DatabaseAdminEndpointService` serves
Web.Health's `/healthz`, `/readyz`, and `/livez`. Under `/cohesion/v1`, only the namespaced
health aliases (`healthz`, `readyz`, `livez`) answer, and only to loopback callers; every other
`/cohesion/v1` route, the retired resource control plane, returns 403.
Configure it with `AddHealthEndpoint(Uri)`. When `COHESION_GATEWAY` is nonblank, a bindable plain
HTTP `http` assignment takes precedence. See the [hosting design](docs/DESIGN.md#platform-binding-and-http-health-1790).

The module provisions nothing and knows no schema. A model's engine builder declares
the databases its engine owns (`sql.AddDatabase("orders", ...)` inside `AddSql`), and
the engine's own build opens or creates and provisions them before it returns, inside
the application's `Build()`; servers start only when the application starts, so
provisioning still precedes accept.

Every composition input is a registration in `Services`, as in `WebApplicationBuilder`.
An engine is a `DatabaseEngineRegistration` singleton instance carrying its name:
`builder.AddSql(name, ...)`, `AddEngine(name, factory)`, `AddEngine(engine)` and the
model packages' component integrations (`builder.Services.AddSql(name, ...)`,
`AddDocuments`, `AddGraph`, `AddKeyValue`, `AddBlob`) all register one. A builder verb
refuses a duplicate name at registration; Build refuses one registered through
`Services` before any engine is built.
The concrete `DatabaseApplicationBuilder.AddService` verb accepts either an
`IHostService` instance or a factory over the final database application context, and
registers it in `Services` as an `IHostService`.
The root builder contract exposes no hosting types or background-work registration (O34).
Build resolves the engines first, then the lifecycle services in registration order,
then appends the health listener, the reopen service and servers. Stop reverses that
complete order, draining servers first; the provider disposes services before engines. Every StartingAsync finishes before any StartAsync.
Concurrent service start/stop options are rejected at construction and lifecycle
settings are snapshotted, so retained mutable options cannot bypass this ordering.

`CreateBuilder()` and `new DatabaseApplicationBuilder(options)` start with an empty
configuration; `CreateBuilder(args)` loads the default sources.

`DatabaseApplicationBuilder` exposes the host-level pieces `WebApplicationBuilder`
does, created the same way: `Environment` (`HostEnvironment`), `Configuration`
(`ConfigurationManager`), `Logging` (`LoggerFactoryBuilder`) and `Services`
(`ServiceProviderBuilder`). Build registers the environment, the configuration and
the logger factory in the one provider, and an owned engine factory
(`AddEngine(name, factory)`) receives their built counterparts in
`DatabaseApplicationBuildContext`. The root `IDatabaseApplicationBuilder` carries
none of them (COHRES004). `Build` creates the application's one provider and
logger factory: building `Services` or `Logging` directly yields a separate
instance the application never uses, and building `Logging` makes the
application's `Build` fail, as with Web's builder.

While the application runs, its own reopen service (owner decision 22) reopens a
database an engine reports offline through the engine's `OpenDatabaseAsync`, with
exponential backoff and jitter (`Options.ReopenInitialDelay`, one second, up to
`Options.ReopenMaximumDelay`, one minute; `Options.ReopenOfflineDatabases = false`
turns it off). Health stays unhealthy, naming each offline database with its cause
and its failed reopen attempts, until the reopen succeeds; a dropped database is
never reopened, and Stop never waits for an attempt. Databases are reopened side by
side, and one that goes offline again soon after its reopen keeps its backoff:
within its engine's `WorkerFailureWindow`, plus the longest interval among its
workers that can take a database offline, plus `Options.ReopenMaximumDelay` (460 s
at the defaults), and only while it is the instance the service reopened (owner
decision 48, as its review revised it; the formula awaits the owner's acceptance).
Every finding, attempt and
outcome is an event of the `Assimalign.Cohesion.Database.Hosting` event source
(`docs/DESIGN.md`, "Reopening offline databases" and "Diagnostics").

The private cross-area `Web.Hosting` and `Web.Health` references implement the
HTTP delivery seam without exposing Web types from Database public APIs. Model
wire protocols remain independent and are still composed through
`DatabaseServer`.
