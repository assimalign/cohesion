# LoadBalancerApplication

Namespace: `Assimalign.Cohesion.LoadBalancer.Hosting`
Assembly: `Assimalign.Cohesion.LoadBalancer.Hosting`

## Purpose

`LoadBalancerApplication` is the public factory for the load balancer hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `ILoadBalancerApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder materializes its explicitly registered host services in registration order; no services are registered automatically.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`. Building throws `InvalidOperationException` when a registered service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.LoadBalancer;
using Assimalign.Cohesion.LoadBalancer.Hosting;

ILoadBalancerApplicationBuilder builder = LoadBalancerApplication.CreateBuilder(args);
await using ILoadBalancerApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
