# Assimalign.Cohesion.Database.Hosting

The Database area's runtime composition root. `DatabaseApplication` hosts the
registered database servers and additional host services; it never references
an ApplicationModel package.

The retired resource integration (the enabled resource's control-plane registration, its
ambient endpoints and bootstrap credential, and the admin listener the resource bound) was
removed on 2026-10-31 with the application-model restart (assimalign/cohesion#1759), and the
area's resource integration returns as `Assimalign.Cohesion.Database.Hosting.ApplicationModel`.
The admin endpoint service is kept for it: Web.Health serves `/healthz`, `/readyz`, and
`/livez` there, readiness stays unhealthy until every wire server has bound, and every
`/cohesion/v1` route fails closed. Nothing starts it yet.

The module provisions nothing and knows no schema. A model's engine builder declares
the databases its engine owns (`sql.AddDatabase("orders", ...)` inside `AddSql`), and
the engine's own build opens or creates and provisions them before it returns, inside
the application's `Build()`; servers start only when the application starts, so
provisioning still precedes accept. Every engine is registered by name (`AddSql(name, ...)`,
`AddEngine(name, factory)`), and a duplicate name is refused at registration, before
any engine is built.
The concrete `DatabaseApplicationBuilder.AddService` verb accepts either an
`IHostService` instance or a factory over the final database application context.
The root builder contract exposes no hosting types or background-work registration (O34).
Registered services start in order before all servers and stop in reverse order
after the servers drain.
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
