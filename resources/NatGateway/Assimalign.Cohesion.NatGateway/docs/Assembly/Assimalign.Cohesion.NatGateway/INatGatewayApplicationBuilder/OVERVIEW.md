# INatGatewayApplicationBuilder

Namespace: `Assimalign.Cohesion.NatGateway`
Assembly: `Assimalign.Cohesion.NatGateway`

## Purpose

`INatGatewayApplicationBuilder` is the public composition seam for a NAT gateway application. It exposes `Build()` returning `INatGatewayApplication`.

## Surface and behavior

- `Build()` creates a configured NAT gateway application.

Registrations retain insertion order. The shared host starts the materialized services in that order and stops them in reverse; a builder with no registrations still produces an empty collection. The public concrete `NatGatewayApplicationBuilder` lives in `Assimalign.Cohesion.NatGateway.Hosting`.

## Exceptions

The concrete Hosting builder's `AddService` throws `ArgumentNullException` for a null service or factory. `Build()` throws `InvalidOperationException` when a factory returns null, and otherwise propagates factory failures.

## Usage

```csharp
using Assimalign.Cohesion.NatGateway;
using Assimalign.Cohesion.NatGateway.Hosting;

NatGatewayApplicationBuilder builder = NatGatewayApplication.CreateBuilder(args);
await using NatGatewayApplication application = builder.Build();
```

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `INatGatewayApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `INatGatewayApplicationContext` exposes `ContentRootPath`. `INatGatewayApplicationBuilder` exposes `Build()`; it currently declares no area-specific verbs. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`NatGatewayApplication.CreateBuilder(args)` returns the public concrete `NatGatewayApplicationBuilder`; its `Build()` returns the public `NatGatewayApplication : Host<NatGatewayApplicationContext>`. The public `NatGatewayApplicationContext` implements `INatGatewayApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
