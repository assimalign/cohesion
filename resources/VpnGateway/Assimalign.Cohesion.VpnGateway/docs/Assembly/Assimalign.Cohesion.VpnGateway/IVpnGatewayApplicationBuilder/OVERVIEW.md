# IVpnGatewayApplicationBuilder

Namespace: `Assimalign.Cohesion.VpnGateway`
Assembly: `Assimalign.Cohesion.VpnGateway`

## Purpose

`IVpnGatewayApplicationBuilder` is the public composition seam for a VPN gateway application. It extends `IHostBuilder` while refining `Build()` to return `IVpnGatewayApplication`.

## Surface and behavior

- `Build()` creates a configured VPN gateway application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.VpnGateway.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.VpnGateway;
using Assimalign.Cohesion.VpnGateway.Hosting;

IVpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder(args);
await using IVpnGatewayApplication application = builder.Build();
```
