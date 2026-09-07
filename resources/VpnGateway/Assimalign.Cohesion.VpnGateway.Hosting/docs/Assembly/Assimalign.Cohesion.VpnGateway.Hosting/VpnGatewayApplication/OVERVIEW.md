# VpnGatewayApplication

Namespace: `Assimalign.Cohesion.VpnGateway.Hosting`
Assembly: `Assimalign.Cohesion.VpnGateway.Hosting`

## Purpose

`VpnGatewayApplication` is the public factory for the VPN gateway hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IVpnGatewayApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.VpnGateway;
using Assimalign.Cohesion.VpnGateway.Hosting;

IVpnGatewayApplicationBuilder builder = VpnGatewayApplication.CreateBuilder(args);
await using IVpnGatewayApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
