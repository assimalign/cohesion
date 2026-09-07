# Assimalign.Cohesion.Database.Hosting

The Database area's runtime composition root. `DatabaseApplication` hosts the
registered database servers and additional host services; it never references
an ApplicationModel package.

`DatabaseApplication.CreateBuilder(args)` also honors an enabled resource's
assembly-keyed default control-plane registration. At build time it aggregates
`AddHealthCheck` registrations and host components implementing
`IHealthContributor`, observes ambient endpoints, attaches the host for graceful
stop, and binds a private Web health host to the ambient `admin` endpoint.
Health, readiness, and liveness are served under `/cohesion/v1/*`.

The no-argument and options overloads remain plain-host entry points. They do
not create a control plane or bind an admin listener.

The private cross-area `Web.Hosting` and `Web.Health` references implement the
HTTP delivery seam without exposing Web types from Database public APIs. Model
wire protocols remain independent and are still composed through
`IDatabaseServer`.
