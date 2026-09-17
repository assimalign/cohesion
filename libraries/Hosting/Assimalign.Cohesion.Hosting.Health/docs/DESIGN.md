# Assimalign.Cohesion.Hosting.Health Design

## Design intent

Health is a data and evaluation contract, not a hosting feature. Keeping it in a Core-only sibling
allows control planes, HTTP adapters, tests, and other consumers to exchange health snapshots
without taking a dependency on host lifecycle or resource-process orchestration.

## Dependency direction

The assembly directly references only `Assimalign.Cohesion.Core`. It does not reference plain
Hosting, Hosting.Resources, dependency injection, Cohesion Web, or a serialization package.
Hosting.Resources depends on Health to aggregate contributors; Web.Health depends on Health to
adapt contributions to its transport-specific model.

The base `Assimalign.Cohesion.App` framework delivers both Hosting siblings because opt-in SDK
generated code calls `Hosting.Resources.ResourceRuntime`; Resources travels beside plain Hosting,
and Health follows transitively. Delivery does not activate the resource runtime and does not alter
this assembly's Core-only dependency boundary.

## Status ordering and defaults

`HealthStatus` is ordered from least to most healthy:

`Unhealthy = 0` < `Degraded = 1` < `Healthy = 2`

The numeric values are part of the public contract and must not be renumbered. This design gives
aggregators a simple least-value rule and ensures default-initialized `HealthContribution` fails
closed rather than reporting a false healthy state.

## Contribution shape

`HealthContribution` is a readonly record struct so a snapshot is immutable, comparable, and cheap
to return synchronously. It contains only:

- `HealthStatus Status`;
- an optional human-readable `Description`; and
- an optional `IReadOnlyDictionary<string, object>` of diagnostic data.

The three factory methods are convenience constructors, not distinct result types. The package
does not constrain diagnostic serialization because transport adapters own that concern.

## Contributor seam

`IHealthContributor` has two members: a stable `Name` and cancellable
`ValueTask<HealthContribution> CheckAsync`. `ValueTask` permits an in-memory contributor to return a
snapshot without allocating a `Task` while retaining asynchronous support for real probes.

The interface intentionally has no registry, priority, tags, retries, timeout, or transport shape.
The composing runtime owns contributor uniqueness, lifetime, concurrency, cancellation budget, and
failure policy. In the resource family, that runtime is Hosting.Resources' control plane.

## Resource snapshot types

`ResourceHealthCheck` names the callable shape used to evaluate a resource health contribution.
`ResourceHealthReport` is the aggregate envelope: one resulting status plus the named
contribution snapshot. Keeping these carrier types beside the contributor contract lets control
plane producers and transport consumers agree without referencing one another.

The report does not compute its own status. Hosting.Resources currently defines aggregation as the
least reported status, with an empty contribution set considered healthy. That is runtime policy,
not behavior embedded in the Health data types.

## AOT and API constraints

All contracts are statically typed and require no reflection, scanning, runtime activation, or
dynamic code generation. Public members retain XML documentation because this assembly is intended
to be consumed directly rather than only through the broader Hosting family.

## Non-goals

- Starting or stopping hosts.
- Registering contributors or maintaining process-wide health state.
- Exposing an HTTP endpoint or adapting to Cohesion Web health checks.
- Parsing resource environments, handling signals, or assigning process exit codes.
