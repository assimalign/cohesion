# Assimalign.Cohesion.Health.Hosting

The hosting seam for Cohesion health checks: the periodic publisher that rides the Hosting
execution menu as a `BackgroundService`, plus the builder-time DI extensions
(`AddHealthChecks`, `AddHealthCheckPublisher`).

This is the only place health touches dependency injection — the core
`Assimalign.Cohesion.Health` library is container-free.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
