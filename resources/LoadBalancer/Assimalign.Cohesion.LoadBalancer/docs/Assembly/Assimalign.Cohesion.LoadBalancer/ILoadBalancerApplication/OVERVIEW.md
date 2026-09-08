# ILoadBalancerApplication

Namespace: `Assimalign.Cohesion.LoadBalancer`
Assembly: `Assimalign.Cohesion.LoadBalancer`

## Purpose

`ILoadBalancerApplication` is the public lifecycle contract for a load balancer application. It extends `IHost` and adds the run-until-shutdown operation used by executable resources.

## Surface

- `RunAsync(CancellationToken cancellationToken = default)` starts the host, waits for shutdown, and completes after the host has stopped.
- The inherited `IHost` members expose the host identity, context, start, stop, and disposal lifecycle.

The current filler implementation registers no hosted services by default. Services explicitly added through `ILoadBalancerApplicationBuilder` participate in the same lifecycle. A token that is already cancelled still drives the host through a clean start-and-stop transition; cancellation after startup requests graceful shutdown.

## Exceptions

`RunAsync` throws `ObjectDisposedException` when invoked after the application has been disposed. Lifecycle failures are propagated to the caller.

## Usage

```csharp
using Assimalign.Cohesion.LoadBalancer;
using Assimalign.Cohesion.LoadBalancer.Hosting;

await using ILoadBalancerApplication application =
    LoadBalancerApplication.CreateBuilder(args).Build();

await application.RunAsync(cancellationToken);
```
