# Hosting.Health

`Assimalign.Cohesion.Hosting.Health` contains Cohesion's transport-neutral health contracts. It can
be used without the host lifecycle package and directly depends only on `Assimalign.Cohesion.Core`.

The package defines `IHealthContributor`, `HealthContribution`, `HealthStatus`,
`ResourceHealthCheck`, and `ResourceHealthReport`. Contributors are named asynchronous snapshot
providers; contributions carry status, an optional description, and optional diagnostic data.

`HealthStatus` is deliberately ordered `Unhealthy = 0`, `Degraded = 1`, `Healthy = 2`. The default
value therefore fails closed, and an aggregator can select its least healthy member. This package
defines the data contract only: contributor registration and aggregation live in
`Assimalign.Cohesion.Hosting.Resources`, while HTTP adaptation lives in
`Assimalign.Cohesion.Web.Health`.

The base `Assimalign.Cohesion.App` framework delivers this assembly because it delivers
Hosting.Resources for opt-in SDK-generated `ResourceRuntime` calls, and Resources depends on
Health. That framework presence does not activate resource behavior; direct package consumers can
still use Health independently.
