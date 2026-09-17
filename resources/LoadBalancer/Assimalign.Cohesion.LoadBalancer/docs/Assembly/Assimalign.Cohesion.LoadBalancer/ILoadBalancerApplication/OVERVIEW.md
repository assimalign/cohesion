# ILoadBalancerApplication

Namespace: `Assimalign.Cohesion.LoadBalancer`
Assembly: `Assimalign.Cohesion.LoadBalancer`

## Purpose and surface

The root contracts are hosting-free (O34): `ILoadBalancerApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `ILoadBalancerApplicationContext` exposes `ContentRootPath`. `ILoadBalancerApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`StartAsync(CancellationToken)` starts the application; `StopAsync(CancellationToken)` drains and stops it. Lifecycle failures propagate to the caller. The contract carries no host identity, runner, or disposal members.

## Hosting implementation

`LoadBalancerApplication.CreateBuilder(args)` returns the public concrete `LoadBalancerApplicationBuilder`; its `Build()` returns the public `LoadBalancerApplication : Host<LoadBalancerApplicationContext>`. The public `LoadBalancerApplicationContext` implements `ILoadBalancerApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

## Usage

```csharp
using Assimalign.Cohesion.LoadBalancer.Hosting;

LoadBalancerApplicationBuilder builder = LoadBalancerApplication.CreateBuilder(args);
// Add area declarations and optional hosting services before Build().
await using LoadBalancerApplication application = builder.Build();
await application.RunAsync();
```
