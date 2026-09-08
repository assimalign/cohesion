# INatGatewayApplicationBuilder

Namespace: `Assimalign.Cohesion.NatGateway`
Assembly: `Assimalign.Cohesion.NatGateway`

## Purpose

`INatGatewayApplicationBuilder` is the public composition seam for a NAT gateway application. It extends `IHostBuilder` while refining `Build()` to return `INatGatewayApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing service instance.
- `AddService(Func<IHostContext, IHostService> factory)` registers a factory that is invoked once per build against the new NatGateway context.
- `Build()` creates a configured NAT gateway application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The concrete builder remains internal to `Assimalign.Cohesion.NatGateway.Hosting`.

## Exceptions

`AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.NatGateway;
using Assimalign.Cohesion.NatGateway.Hosting;

INatGatewayApplicationBuilder builder = NatGatewayApplication.CreateBuilder(args);
await using INatGatewayApplication application = builder.Build();
```
