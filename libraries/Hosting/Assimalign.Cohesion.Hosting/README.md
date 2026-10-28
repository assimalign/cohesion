# Hosting

`Assimalign.Cohesion.Hosting` is Cohesion's dependency-light host lifecycle package. It owns host
construction, service startup and shutdown, restartable run state, and the complete run:
`RunAsync` starts the host, waits for a shutdown request or signal, and drains it. Its only
Cohesion assembly dependency is `Assimalign.Cohesion.Core`.

The package deliberately does not own resource behavior or health contracts. Direct package
consumers can add the sibling packages only when those capabilities are needed:

| Package | Responsibility | Direct dependencies |
| --- | --- | --- |
| `Assimalign.Cohesion.Hosting` | Host lifecycle, contexts, services, and the complete run | Core |
| `Assimalign.Cohesion.Hosting.Health` | Transport-neutral health contracts and snapshots | Core |
| `Assimalign.Cohesion.Hosting.Resources` | Resource instances: actions and state, the control plane, manifest publication, and the resource service that joins an instance to its gateway | Core, Hosting |

The base `Assimalign.Cohesion.App` framework delivers both siblings beside plain Hosting.
Delivery is not activation: an application becomes a managed resource only when its area's
Hosting.Resources package registers a resource service.

While `RunAsync` runs, the process's shutdown signals (SIGINT, SIGQUIT and SIGTERM) request a
graceful shutdown instead of ending the process, exactly as cancelling its token does. A platform's
stop, such as Kubernetes' SIGTERM, therefore drains the host within `ShutdownTimeout`, and
`RunAsync` returns. A direct `StopAsync` and the active `RunAsync` join the same stop operation and
observe the same stop failure.

Any `IHost` can be nested through `AsService()`. The parent starts the child with `StartAsync`, so a
nested host handles no signals: the outer run owns them. The parent awaits the child's `StartAsync`
readiness boundary, passes its remaining startup and shutdown budgets through the service adapter,
and stops nested hosts in reverse registration order. Child startup and drain failures therefore
surface from the parent's complete `RunAsync` task.
