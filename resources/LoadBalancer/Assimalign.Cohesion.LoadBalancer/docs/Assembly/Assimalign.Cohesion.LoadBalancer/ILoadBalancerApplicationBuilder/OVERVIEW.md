# ILoadBalancerApplicationBuilder

Namespace: `Assimalign.Cohesion.LoadBalancer`
Assembly: `Assimalign.Cohesion.LoadBalancer`

## Purpose

`ILoadBalancerApplicationBuilder` is the public composition seam for a load balancer application. It extends `IHostBuilder` while refining `Build()` to return `ILoadBalancerApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `Build()` creates a configured load balancer application.

The current filler builder has no area feature or service registrations by default. Explicit services are exposed in registration order, start in that order, and stop in reverse order. The concrete builder remains internal to `Assimalign.Cohesion.LoadBalancer.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.LoadBalancer;
using Assimalign.Cohesion.LoadBalancer.Hosting;

ILoadBalancerApplicationBuilder builder = LoadBalancerApplication.CreateBuilder(args);
await using ILoadBalancerApplication application = builder.Build();
```
