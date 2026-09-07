# NatGatewayApplication

Namespace: `Assimalign.Cohesion.NatGateway.Hosting`
Assembly: `Assimalign.Cohesion.NatGateway.Hosting`

## Purpose

`NatGatewayApplication` is the public factory for the NAT gateway hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `INatGatewayApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.NatGateway;
using Assimalign.Cohesion.NatGateway.Hosting;

INatGatewayApplicationBuilder builder = NatGatewayApplication.CreateBuilder(args);
await using INatGatewayApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
