# Assimalign.Cohesion.ApplicationModel.Gateway — Overview

## Purpose

Runs an application model: deploys the gateway's part of the hierarchy through an infrastructure
provider in dependency order, each resource once what it depends on is ready, receives the
manifests the deployed instances publish, and exposes each resource's aggregate and infrastructure
actions. The design record is [DESIGN.md](DESIGN.md).

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

// east deploys once identity has published and its outputs are read.
builder.AddGateway("east").AddResource("Web", "orders-api").DependsOn(identity, "tokens.issue");

await builder.Build().RunAsync();
```

A child gateway starts from the deployment manifest its parent's provider passed it:

```csharp
GatewayDeploymentManifest deployment = GatewayDeploymentManifest.Parse(json);
ApplicationGateway gateway = ApplicationGateway.Create(infrastructure);

await gateway.RunAsync(deployment.ToModel(), cancellationToken);
```

A transport hands each publication to `gateway.PublishAsync(manifest)` and returns its snapshots;
an in-process resource uses the gateway as its `IResourceManifestPublisher`.
