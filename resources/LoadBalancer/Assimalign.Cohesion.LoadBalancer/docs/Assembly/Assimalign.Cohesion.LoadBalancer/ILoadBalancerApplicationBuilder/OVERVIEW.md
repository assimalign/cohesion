# ILoadBalancerApplicationBuilder

Namespace: `Assimalign.Cohesion.LoadBalancer`
Assembly: `Assimalign.Cohesion.LoadBalancer`

## Purpose

`ILoadBalancerApplicationBuilder` is the public composition seam for a load balancer application. It extends `IHostBuilder` while refining `Build()` to return `ILoadBalancerApplication`.

## Surface and behavior

- `Build()` creates a configured load balancer application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.LoadBalancer.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.LoadBalancer;
using Assimalign.Cohesion.LoadBalancer.Hosting;

ILoadBalancerApplicationBuilder builder = LoadBalancerApplication.CreateBuilder(args);
await using ILoadBalancerApplication application = builder.Build();
```
