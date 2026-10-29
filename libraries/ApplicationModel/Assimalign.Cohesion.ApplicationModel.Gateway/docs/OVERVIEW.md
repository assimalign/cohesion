# Assimalign.Cohesion.ApplicationModel.Gateway — Overview

## Purpose

Runs an application model: deploys the gateway's part of the hierarchy through an infrastructure
provider in dependency order, each resource once what it depends on is ready and with its settings
filled in from their outputs, receives the manifests the deployed instances publish, and exposes
each resource's aggregate and infrastructure actions. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `ApplicationGateway`, the `IInfrastructureProvider` seam, `GatewayDeploymentManifest`,
  the `IGatewayStore` record with its JSON file store, and `ApplicationGatewayException`.
- Out of scope: HTTP (the publication endpoint and management routes are transport-neutral here),
  platform providers, and identity and authentication.

## Dependencies

`Assimalign.Cohesion.ApplicationModel` and `Assimalign.Cohesion.Hosting.Resources`.

## Usage

The root gateway composes its application and runs it through a provider for its platform:

```csharp
ApplicationGateway gateway = ApplicationGateway.Create(infrastructure, startupTimeout: TimeSpan.FromMinutes(2));

ApplicationBuilder builder = Application.CreateBuilder("root", args).UseGateway(gateway);
IApplicationResourceDescriptor identity = builder.AddResource("IdentityHub", "identity");

// east deploys once identity has published and its outputs are read, with identity's endpoint in
// its deployment manifest. Inside east, orders-api deploys once orders-db is ready, with both
// endpoints in its configuration.
IApplicationSet east = builder.AddGateway("east");
IApplicationResourceDescriptor database = east.AddResource("Database.Sql", "orders-db");
east.AddResource("Web", "orders-api")
    .DependsOn(identity, "tokens.issue")
    .WithSetting("Identity:Authority", identity.Endpoint)
    .WithSetting("Database:Endpoint", database.Endpoint);

await builder.Build().RunAsync();
```

A child gateway starts from the deployment manifest its parent wrote and its parent's provider
passed it. The manifest carries the values of the parent's outputs that settings in the child's part
use, such as the identity provider's endpoint:

```csharp
GatewayDeploymentManifest deployment = GatewayDeploymentManifest.Parse(json);
ApplicationGateway gateway = ApplicationGateway.Create(infrastructure);

await gateway.RunAsync(deployment.ToModel(), cancellationToken);
```

A transport hands each publication to `gateway.PublishAsync(manifest)` and returns its snapshots;
an in-process resource uses the gateway as its `IResourceManifestPublisher`.
