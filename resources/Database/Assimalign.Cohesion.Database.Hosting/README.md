# Assimalign.Cohesion.Database.Hosting

The Database area's runtime composition root. `DatabaseApplication` hosts the
registered database servers and additional host services; it never references
an ApplicationModel package.

`DatabaseApplication.CreateBuilder(args)` also honors an enabled resource's
assembly-keyed default control-plane registration. At build time it aggregates
`AddHealthCheck` registrations and host components implementing
the `Hosting.Health` `IHealthContributor` contract with the application context's
engine/worker health, observes ambient `Hosting.Resources` endpoints, attaches the host
for graceful stop, and binds a
private Web host to the ambient `admin` endpoint. Web.Health serves `/healthz`,
`/readyz`, and `/livez`; the registered control plane serves
`/cohesion/v1/endpoints`, `/cohesion/v1/stop`, and
`/cohesion/v1/commands` (with no Database command kinds until item 31c). The
namespaced control-plane surface requires the ambient bootstrap bearer credential;
gateway-scoped invocations fail closed when that credential is absent. The bare
health/probe routes remain reachable by platform probes. Readiness stays unhealthy
until the outer Database host reaches `Started`, which means every wire server has
completed its accepting bind; liveness remains process-oriented.

`builder.Provision(engine, name)` opens or creates a declared database before
accept. `builder.AddDatabase(engine, name, configure)` also retains its C# schema
for the later schema-compilation and migration stages. These remain before-accept
operations regardless of fluent verb order because additional services always
start before server wrappers. Creation follows only an exact
`DatabaseNotFoundException`; other open failures abort startup.
The concrete `DatabaseApplicationBuilder.AddService` verb accepts either an
`IHostService` instance or a factory over the final database application context.
The root builder contract exposes no hosting types or background-work registration (O34).
Registered services start in order before all servers and stop in reverse order
after the servers drain.
Concurrent service start/stop options are rejected at construction and lifecycle
settings are snapshotted, so retained mutable options cannot bypass this ordering.

The no-argument and options overloads remain plain-host entry points. They do
not create a control plane or bind an admin listener, and start with an empty
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
