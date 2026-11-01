# Assimalign.Cohesion.ApplicationModel.Definition — Overview

## Purpose

Declares what one gateway runs: an application set of resources with permission-carrying
dependencies and settings, and the parent gateway it joins. It validates the declarations into an
immutable model. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `IApplicationModelBuilder` and `IApplicationModel` with their implementations;
  `IApplicationSet`; `IApplicationModelParent`; `IApplicationResourceDefinition` with `DependsOn`,
  `WithSetting`, `WithExtension`, and its output placeholders; and the manifest records.
- Out of scope: gateways, their deployments of resources, platforms, and infrastructure actions
  (ApplicationModel.Gateway); running instances (ApplicationModel.Hosting); the identity provider;
  area verbs such as `AddWeb`.

## Dependencies

`Assimalign.Cohesion.Core` and `Assimalign.Cohesion.ApplicationModel`, the resource identity it
shares with gateways and running instances. It never references the gateway-to-instance protocol
(ApplicationModel.Protocol).

## Usage

Declare one gateway's model. A child gateway names its parent and reads the parent's outputs:

```csharp
IApplicationModelBuilder east = ApplicationModel.CreateBuilder("east");
IApplicationModelParent root = east.UseParent("root");

IApplicationResourceDefinition database = east.AddResource("Database.Sql", "orders-db");
IApplicationResourceDefinition ordersApi = east.AddResource("Web", "orders-api")
    .DependsOn(database, "data.read", "data.write")
    .WithSetting("Database:Endpoint", database.Endpoint)                            // filled in when east deploys it
    .WithSetting("Identity:Authority", root.Endpoint("IdentityHub", "identity"))    // the root answers east's join with it
    .WithSetting("Logging:LogLevel:Default", "Information");

// A platform package's verb, here a Kubernetes package's, records how its platform deploys the
// resource; only a gateway on that platform applies it. It calls the generic form:
// WithExtension("kubernetes", "autoscaler", JsonSerializer.SerializeToElement(options, context)).
ordersApi.ConfigureHpa(hpa => hpa.MaxReplicas = 10);

IApplicationModel model = east.Build();          // composition closes here
ApplicationModelManifest manifest = model.Manifest;   // what east sends the root when it joins
```

A gateway program uses ApplicationModel.Gateway's `ApplicationGatewayBuilder`, which declares the model this
way and builds the gateway that runs it. Manifest data, such as the manifest a child sends its
parent, becomes the same model:

```csharp
ApplicationModel model = ApplicationModel.Create(manifest);
```
