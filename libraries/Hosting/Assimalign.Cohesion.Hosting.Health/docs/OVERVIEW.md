# Assimalign.Cohesion.Hosting.Health

## Summary

Defines the small, transport-neutral health vocabulary shared by Cohesion resource runtimes,
delivery adapters, and consumers. The contracts were separated from plain Hosting so health-only
code does not acquire host lifecycle or resource-process behavior.

## Package boundary

- Direct Cohesion dependency: `Assimalign.Cohesion.Core` only.
- No dependency on `Assimalign.Cohesion.Hosting` or Hosting.Resources.
- No contributor registry, ambient context, process protocol, or signal handling.
- No dependency-injection or HTTP health-check dependency.
- NativeAOT-compatible, reflection-free data contracts.

The base `Assimalign.Cohesion.App` framework includes Health because it includes Hosting.Resources
for opt-in SDK-generated `ResourceRuntime` calls and Health is in that package's dependency closure.
This delivery choice does not couple Health back to Hosting or activate resource behavior.

## Contracts

### `HealthStatus`

An ordered status enum whose numeric values are contractual:

| Value | Number | Meaning |
| --- | ---: | --- |
| `Unhealthy` | 0 | Unavailable or not functioning |
| `Degraded` | 1 | Functioning with reduced capability or performance |
| `Healthy` | 2 | Functioning normally |

The ordering supports least-healthy aggregation and makes the default enum value unhealthy.

### `HealthContribution`

A readonly record struct containing `Status`, optional `Description`, and optional diagnostic
`Data`. The `Healthy`, `Degraded`, and `Unhealthy` factories preserve the same payload while making
the intended status explicit. Its default value fails closed as unhealthy with no description or
data.

### `IHealthContributor`

A named provider of `ValueTask<HealthContribution>`. `Name` is the stable aggregate key, and
`CheckAsync` supports both allocation-free synchronous snapshots and cancellable asynchronous
checks. Contributor lifetime and uniqueness policy belong to the composing runtime.

### Resource snapshot contracts

`ResourceHealthCheck` is a cancellable delegate returning one contribution.
`ResourceHealthReport` carries an aggregate `HealthStatus` and a snapshot dictionary keyed by
contributor name. These types do not perform registration or aggregation themselves.

## Ownership map

| Concern | Owning package |
| --- | --- |
| Health values and contributor interface | `Assimalign.Cohesion.Hosting.Health` |
| Host lifecycle and complete-run pipeline | `Assimalign.Cohesion.Hosting` |
| Contributor registry and resource aggregation | `Assimalign.Cohesion.Hosting.Resources` |
| Cohesion Web health-check adaptation | `Assimalign.Cohesion.Web.Health` |
