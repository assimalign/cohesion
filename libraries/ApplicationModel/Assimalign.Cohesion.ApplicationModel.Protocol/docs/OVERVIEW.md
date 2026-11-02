# Assimalign.Cohesion.ApplicationModel.Protocol — Overview

## Purpose

Declares the protocol between a gateway and the instances of its resources, so both sides speak
through the same types without either loading the other's runtime. Every type here is part of a
message the two exchange, or of the one seam both sides use. The design record is
[DESIGN.md](DESIGN.md).

## Scope

- In scope: what an instance publishes (`ApplicationInstanceManifest`, `ApplicationActionDescriptor`,
  `InstanceKey`, `DeploymentId`) and what its gateway answers (`ApplicationInstancePublication`);
  the publisher seam (`IApplicationInstancePublisher`); action invocation (`ActionName`,
  `ApplicationActionKind`, `ApplicationActionOperation`, `ApplicationActionContext`,
  `ApplicationActionResult`, `ApplicationActionStatus`); state (`ApplicationActionSnapshot`); and
  content (`IApplicationContentSerializer`, `ApplicationContentException`, and the default
  `JsonApplicationContentSerializer`).
- Out of scope: resource and platform identity (the root `Assimalign.Cohesion.ApplicationModel`);
  the logical model (ApplicationModel.Definition); the seams only one side uses, such as the
  instance's control plane and gateway client (ApplicationModel.Hosting) and the gateway's instance
  client (ApplicationModel.Gateway); and every transport, which belongs to the platform packages.

## Dependencies

`Assimalign.Cohesion.Core` and `Assimalign.Cohesion.ApplicationModel`. It compiles the root's
`shared/Names/` grammar for `ActionName`.

## Usage

An instance publishes its manifest through its platform's client, and its gateway answers with the
snapshots to restore:

```csharp
ApplicationInstanceManifest manifest = new(
    "Web",
    "orders-api",
    InstanceKey.New(),
    Deployment: null,
    ControlPlaneEndpoint: null,
    [new ApplicationActionDescriptor("log-level", ApplicationActionKind.ReadWrite, IsStateful: true)]);
ApplicationInstancePublication publication = await publisher.PublishAsync(manifest, cancellationToken);
```

An operator's invocation requests one operation of an action:

```csharp
JsonApplicationContentSerializer json = JsonApplicationContentSerializer.Create(MyJsonContext.Default);
ApplicationActionContext context = ApplicationActionContext.Create(
    ApplicationActionOperation.Write,
    json,
    json.Serialize(new LogLevelSetting("Debug")));
```
