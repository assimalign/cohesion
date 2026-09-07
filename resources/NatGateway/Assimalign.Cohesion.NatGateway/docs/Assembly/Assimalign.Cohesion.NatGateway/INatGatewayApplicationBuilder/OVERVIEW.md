# INatGatewayApplicationBuilder

Namespace: `Assimalign.Cohesion.NatGateway`
Assembly: `Assimalign.Cohesion.NatGateway`

## Purpose

`INatGatewayApplicationBuilder` is the public composition seam for a NAT gateway application. It extends `IHostBuilder` while refining `Build()` to return `INatGatewayApplication`.

## Surface and behavior

- `Build()` creates a configured NAT gateway application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.NatGateway.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.NatGateway;
using Assimalign.Cohesion.NatGateway.Hosting;

INatGatewayApplicationBuilder builder = NatGatewayApplication.CreateBuilder(args);
await using INatGatewayApplication application = builder.Build();
```
