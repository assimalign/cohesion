# ILoadBalancerApplicationBuilder

Namespace: `Assimalign.Cohesion.LoadBalancer`
Assembly: `Assimalign.Cohesion.LoadBalancer`

## Purpose

`ILoadBalancerApplicationBuilder` is the public composition seam for a load balancer application. It exposes `Build()` returning `ILoadBalancerApplication`.

## Surface and behavior

- `Build()` creates a configured load balancer application.

The current filler builder has no area feature or service registrations by default. Services registered on the concrete Hosting builder start in registration order and stop in reverse order. The public concrete `LoadBalancerApplicationBuilder` lives in `Assimalign.Cohesion.LoadBalancer.Hosting`.

## Exceptions

The concrete Hosting builder's `AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a service factory returns null.

## Usage

```csharp
using Assimalign.Cohesion.LoadBalancer;
using Assimalign.Cohesion.LoadBalancer.Hosting;

LoadBalancerApplicationBuilder builder = LoadBalancerApplication.CreateBuilder(args);
await using LoadBalancerApplication application = builder.Build();
```

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `ILoadBalancerApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ILoadBalancerApplicationContext` exposes `ContentRootPath`. `ILoadBalancerApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`LoadBalancerApplication.CreateBuilder(args)` returns the public concrete `LoadBalancerApplicationBuilder`; its `Build()` returns the public `LoadBalancerApplication : Host<LoadBalancerApplicationContext>`. The public `LoadBalancerApplicationContext` implements `ILoadBalancerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
