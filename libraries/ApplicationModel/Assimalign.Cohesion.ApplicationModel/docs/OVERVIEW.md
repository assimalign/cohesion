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
  (`IApplicationContentSerializer`, `ApplicationContentException`, and the default
  `JsonApplicationContentSerializer`); the platform name (`PlatformName`); and the two client seams a
  platform implements: publication (`IApplicationInstancePublisher`, `ApplicationInstancePublication`)
  and the gateway's calls to an instance (`IApplicationInstanceClient`).
- Out of scope: the logical model (ApplicationModel.Definition), the instance runtime
  (ApplicationModel.Hosting), gateways (ApplicationModel.Gateway), and every transport, which belongs
  to the platform packages (ApplicationModel.Hosting.InProcess and ApplicationModel.Gateway.InProcess
  for the in-process platform).

## Dependencies

`Assimalign.Cohesion.Core` only.

## Usage

An instance publishes its manifest through its platform's client, and its gateway answers with the
snapshots to restore; the client names where the gateway reaches the control plane:

```csharp
ApplicationInstanceManifest manifest = new(
    "Web",
    "orders-api",
    InstanceKey.New(),
    Deployment: null,
    ControlPlaneEndpoint: null,
    actions);
ApplicationInstancePublication publication = await publisher.PublishAsync(manifest, cancellationToken);
```

Every form of a resource derives the same id from its provider and name:

```csharp
ResourceId id = ResourceId.FromName("Web", "orders-api");
```
