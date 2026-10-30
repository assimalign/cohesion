# Assimalign.Cohesion.ApplicationModel — Overview

## Purpose

Declares what both sides of the application model exchange, so a gateway and the instances it runs
speak through the same types without either loading the other's code. The design record is
[DESIGN.md](DESIGN.md).

## Scope

- In scope: `IApplicationResource`, the base of a resource's definition, deployment, and instances;
  the identity value objects (`ProviderName`, `ProviderId`, `ResourceName`, `ResourceId`,
  `InstanceKey`, `ActionName`, `DeploymentId`); the instance manifest (`ApplicationInstanceManifest`,
  `ApplicationActionDescriptor`, `ApplicationActionKind`); invocation (`ApplicationActionContext`,
  `ApplicationActionResult`, `ApplicationActionStatus`); state (`ApplicationActionSnapshot`); content
  (`IApplicationContentSerializer`, `ApplicationContentException`); and publication
  (`IApplicationInstancePublisher`, `ApplicationInstancePublication`).
- Out of scope: the logical model (ApplicationModel.Definition), the instance runtime and its HTTP
  publisher (ApplicationModel.Hosting), and gateways (ApplicationModel.Gateway).

## Dependencies

`Assimalign.Cohesion.Core` only.

## Usage

An instance publishes its manifest through the seam, and its gateway answers with the snapshots to
restore:

```csharp
ApplicationInstanceManifest manifest = new(
    "Web",
    "orders-api",
    InstanceKey.New(),
    Deployment: null,
    ControlPlaneEndpoint: new Uri("http://10.0.0.5:9090/"),
    actions);
ApplicationInstancePublication publication = await publisher.PublishAsync(manifest, cancellationToken);
```

Every form of a resource derives the same id from its provider and name:

```csharp
ResourceId id = ResourceId.FromName("Web", "orders-api");
```
