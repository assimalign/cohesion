# IVpnGatewayApplicationBuilder

Namespace: `Assimalign.Cohesion.VpnGateway`
Assembly: `Assimalign.Cohesion.VpnGateway`

## Purpose

`IVpnGatewayApplicationBuilder` is the public composition seam for a VPN gateway application. It extends `IHostBuilder` while refining `Build()` to return `IVpnGatewayApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new VpnGateway context.
- `Build()` creates a configured VPN gateway application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.VpnGateway.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.VpnGateway;
using Assimalign.Cohesion.VpnGateway.Hosting;

IVpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder(args);
await using IVpnGatewayApplication application = builder.Build();
```
