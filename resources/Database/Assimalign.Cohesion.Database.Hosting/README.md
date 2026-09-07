# Assimalign.Cohesion.Database.Hosting

The Database area's runtime composition root. `DatabaseApplication` hosts the
registered database servers and additional host services; it never references
an ApplicationModel package.

`DatabaseApplication.CreateBuilder(args)` also honors an enabled resource's
assembly-keyed default control-plane registration. At build time it aggregates
`AddHealthCheck` registrations and host components implementing
`IHealthContributor` with the application context's engine/worker health,
observes ambient endpoints, attaches the host for graceful stop, and binds a
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
Concurrent service start/stop options are rejected at construction and lifecycle
settings are snapshotted, so retained mutable options cannot bypass this ordering.

The no-argument and options overloads remain plain-host entry points. They do
not create a control plane or bind an admin listener.

The private cross-area `Web.Hosting` and `Web.Health` references implement the
HTTP delivery seam without exposing Web types from Database public APIs. Model
wire protocols remain independent and are still composed through
`IDatabaseServer`.
