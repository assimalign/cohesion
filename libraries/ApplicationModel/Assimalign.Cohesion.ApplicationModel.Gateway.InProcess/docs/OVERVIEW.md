# Assimalign.Cohesion.ApplicationModel.Gateway.InProcess — Overview

`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` runs a gateway's resources inside the
gateway's own process. Its `InProcessInfrastructure` is an infrastructure provider (the `inprocess`
platform) and the transport its programs publish through. Each resource is a program the gateway
program references. Deploying a resource calls the program's entry point on a thread of its own, in
an environment of its own.

```csharp
await using var infrastructure = new InProcessInfrastructure();
infrastructure
    .AddProgram("Web", "orders-api", Orders.Program.MainAsync)
    .AddProgram("Web", "storefront", Storefront.Program.MainAsync);

ApplicationBuilder builder = ApplicationGateway.CreateBuilder("local").UseInProcess(infrastructure);
IApplicationResourceDefinition orders = builder.AddResource("Web", "orders-api");
builder.AddResource("Web", "storefront").WithSetting("Orders:Url", orders.Endpoint);

await builder.Build().RunAsync(cancellationToken);
```

Each program is an ordinary application. A Web program calls `WebApplication.CreateBuilder(args)`
and `builder.AddResource(name)`. It listens on the loopback port the infrastructure assigned, reads
its settings from configuration, and publishes to the gateway over the in-process channel. Nothing
in it knows where it runs.

The gateway deploys in dependency order, so `storefront` starts only once `orders-api` has
published, with `Orders:Url` set to the address `orders-api` serves. Programs outlive the gateway's
run: disposing the infrastructure stops them, the last deployed first.

**Depends on:** ApplicationModel.Gateway (`IInfrastructureProvider`, `IApplicationGatewayTransport`,
`ApplicationBuilder`) and ApplicationModel.Hosting (the in-process endpoint). It references no
resource area.

See [DESIGN.md](DESIGN.md) for the environment each program receives, the channel, and the
lifecycle. Public API reference belongs in
[cohesion-docs](https://github.com/assimalign/cohesion-docs).
