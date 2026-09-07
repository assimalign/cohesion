# Hosting

`Assimalign.Cohesion.Hosting` is Cohesion's dependency-light host lifecycle package. It owns host
construction, service startup and shutdown, restartable run state, and the public runner/observer
seam used to wrap one complete host lifetime. Its only Cohesion assembly dependency is
`Assimalign.Cohesion.Core`.

The package deliberately does not own resource-process orchestration or health contracts. Direct
package consumers can add the sibling packages only when those capabilities are needed:

| Package | Responsibility | Direct dependencies |
| --- | --- | --- |
| `Assimalign.Cohesion.Hosting` | Host lifecycle, contexts, services, runners, and observers | Core |
| `Assimalign.Cohesion.Hosting.Health` | Transport-neutral health contracts and snapshots | Core |
| `Assimalign.Cohesion.Hosting.Resources` | Ambient resource context, control plane, entry invocation, and resource-process runner | Core, Hosting, Hosting.Health, ProtectedData |

The base `Assimalign.Cohesion.App` framework delivers both siblings because every opt-in resource
SDK generates calls to `Hosting.Resources.ResourceRuntime`; Resources travels beside plain Hosting,
and Health follows as its dependency. Delivery is not activation: resource behavior remains disabled
until `CohesionApplicationModel=enabled` generates the registration used by an area builder.

Set `HostContext.Runner` to an `IHostRunner` to wrap future calls to `Host.RunAsync`. A runner
receives a one-shot `IHostRun`, may set its `ShutdownTimeout`, and must call `IHostRun.RunAsync` to
execute the host. An optional `IHostRunObserver` sees the transitions that occur in order:
`Started`, `Stopping`, optional `DrainAborted`, and `Stopped`. If a stop is accepted while
`OnStartedAsync` is still running, `Started` is omitted and the sequence begins with `Stopping`.
A direct `StopAsync` and the active `RunAsync` join the same stop operation and observe the same
stop failure.
