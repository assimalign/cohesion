# Assimalign.Cohesion.ApplicationModel.Gateway — Overview

## Purpose

Runs one gateway's application model: deploys its resources through an infrastructure provider in
dependency order, each resource once what it depends on is ready and with its settings filled in from
their outputs or its parent's, receives the manifests the deployed instances publish, lets child
gateways join it, and exposes each resource's deployment and infrastructure actions. The design
record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `ApplicationGateway` and its `ApplicationBuilder`; the `IApplicationGateway`,
  `IApplicationGatewayBuilder`, `IApplicationParentGateway`, `IApplicationGatewayTransport`,
  `IApplicationInstanceClient` (with its default HTTP client for the control-plane binding), and
  `IInfrastructureProvider` seams; an operator's actions on a resource's instances
  (`ApplicationGateway.ExecuteAsync`); the
  deployment of a resource (`IApplicationResourceDeployment`, `ApplicationResourceDeployment`,
  `IApplicationResourceAction`); the `IApplicationGatewayStore` record with its JSON file store; and
  `ApplicationGatewayException`.
- Out of scope: serving HTTP (the publication endpoint, joins, and management routes are
  transport-neutral here; the gateway only calls its instances' control planes over HTTP), platform
  providers, and identity and authentication.

## Dependencies

`Assimalign.Cohesion.ApplicationModel.Definition`, and through it the contracts in `Assimalign.Cohesion.ApplicationModel`.

## Usage

The root gateway declares its model and runs it through a provider for its platform:

```csharp
ApplicationBuilder builder = ApplicationGateway.CreateBuilder("root");
builder.AddResource("IdentityHub", "identity");

ApplicationGateway root = builder.UseInfrastructure(infrastructure).Build();
await root.RunAsync(cancellationToken);
```

A child gateway is its own program. It names its parent and reads the parent's outputs: it joins
the parent before it deploys anything, and its `orders-api` deploys once `orders-db` is ready, with
both endpoints in its configuration:

```csharp
ApplicationBuilder builder = ApplicationGateway.CreateBuilder("east");
IApplicationParent root = builder.UseParent(rootGateway);

IApplicationResourceDefinition database = builder.AddResource("Database.Sql", "orders-db");
builder.AddResource("Web", "orders-api")
    .DependsOn(database, "data.read")
    .WithSetting("Identity:Authority", root.Endpoint("IdentityHub", "identity"))
    .WithSetting("Database:Endpoint", database.Endpoint);

ApplicationGateway east = builder.UseInfrastructure(infrastructure).Build();
await east.RunAsync(cancellationToken);
```

A transport (`builder.UseTransport(transport)`) hands each publication to
`gateway.PublishAsync(manifest)` and returns its snapshots; the gateway starts its transports when it
runs and stops them when the run ends. A child gateway in the same process joins through
`gateway.JoinAsync(manifest)`, using the gateway as its `IApplicationParentGateway`. To run every
resource inside the gateway program's own process, use Gateway.InProcess:
`builder.UseInProcess(infrastructure)` sets its infrastructure, which is also its transport.

An operator manages a resource through the gateway. The action runs on one instance's control plane,
and a write's state is recorded and restored on every other instance; an instance that starts later
restores it when it publishes:

```csharp
ApplicationActionResult result = await gateway.ExecuteAsync(
    "Web",
    "orders-api",
    "cors",
    ApplicationActionContext.Create(ApplicationActionKind.Write, serializer, """{"origins":["https://partner.example"]}"""u8.ToArray()));
```
