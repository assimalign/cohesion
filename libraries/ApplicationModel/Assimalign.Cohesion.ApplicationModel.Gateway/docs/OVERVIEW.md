# Assimalign.Cohesion.ApplicationModel.Gateway — Overview

## Purpose

Runs one gateway's application model: deploys its resources through an infrastructure provider in
dependency order, each resource once what it depends on is ready and with its settings filled in from
their outputs or its parent's, receives the manifests the deployed instances publish, lets child
gateways join it, and exposes each resource's deployment and infrastructure actions. The design
record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `ApplicationGateway` and its `ApplicationGatewayBuilder`; the `IApplicationGateway`
  (start, stop, and `Completion`, with the `RunAsync` extension), `IApplicationGatewayContext`,
  `IApplicationGatewayBuilder`, `IApplicationGatewayParent`, `IApplicationGatewayInfrastructureProvider`
  (which is also the inbound channel), and `IApplicationGatewayInstanceClient` seams; an
  operator's actions on a resource's instances (`ApplicationGateway.ExecuteAsync`); the deployment
  of a resource (`IApplicationGatewayDeployment`, `ApplicationGatewayDeployment`,
  `IApplicationGatewayDeploymentAction`); the `IApplicationGatewayStore` record with its JSON file store; and
  `ApplicationGatewayException`.
- Out of scope: every transport. A platform's gateway half supplies the infrastructure, whose
  channel instances publish through, and the instance client the gateway calls them with, such as
  `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess`. Also out of scope: identity and
  authentication.

## Dependencies

`Assimalign.Cohesion.ApplicationModel.Definition` and `Assimalign.Cohesion.ApplicationModel.Protocol`,
and through both the identity in `Assimalign.Cohesion.ApplicationModel`.

## Usage

The root gateway declares its model and runs it through a provider for its platform:

```csharp
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("root");
builder.AddResource("IdentityHub", "identity");

ApplicationGateway root = builder.UseInfrastructure(infrastructure).Build();
await root.RunAsync(cancellationToken);
```

A child gateway is its own program. It names its parent and reads the parent's outputs: it joins
the parent before it deploys anything, and its `orders-api` deploys once `orders-db` is ready, with
both endpoints in its configuration:

```csharp
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("east");
IApplicationModelParent root = builder.UseParent(rootGateway);

IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "orders-db");
builder.AddResource("Web", "orders-api")
    .DependsOn(database, "data.read")
    .WithSetting("Identity:Authority", root.Endpoint("IdentityHub", "identity"))
    .WithSetting("Database:Endpoint", database.Endpoint);

ApplicationGateway east = builder.UseInfrastructure(infrastructure).Build();
await east.RunAsync(cancellationToken);
```

The infrastructure provider is also the gateway's inbound channel: the gateway starts it with its
context (`provider.StartAsync(gateway.Context)`) when the run begins, the channel hands each
publication to `context.Publisher.PublishAsync(manifest)` and returns its snapshots, and the gateway
stops it when the run ends. A host that drives the run itself calls `StartAsync` and `StopAsync` and
watches `Completion`, which faults when the run fails after startup. A child gateway in the same process joins through
`gateway.JoinAsync(manifest)`, using the gateway as its `IApplicationGatewayParent`. To run every
resource inside the gateway program's own process, use Gateway.InProcess:
`builder.UseInProcess(infrastructure)` sets its infrastructure, which is also its channel and its
instance client.

`UseInfrastructure` and `UseInstanceClient` borrow what they are given: the program disposes it
once the gateway has stopped. `UsePlatform(factory)` hands the builder a factory instead, which
`Build()` calls; the platform it creates is both the infrastructure and the instance client, and the
gateway owns it. Disposing the gateway stops its run and then disposes that platform:

```csharp
await using ApplicationGateway gateway = builder.UsePlatform(() => CreatePlatform()).Build();
await gateway.RunAsync(cancellationToken);
```

An operator manages a resource through the gateway. The action runs on one instance's control plane,
and a write's state is recorded and restored on every other instance; an instance that starts later
restores it when it publishes:

```csharp
ApplicationActionResult result = await gateway.ExecuteAsync(
    "Web",
    "orders-api",
    "cors",
    ApplicationActionContext.Create(ApplicationActionOperation.Write, serializer, """{"origins":["https://partner.example"]}"""u8.ToArray()));
```
