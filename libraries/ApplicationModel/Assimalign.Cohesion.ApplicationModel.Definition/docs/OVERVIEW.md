# Assimalign.Cohesion.ApplicationModel.Definition — Overview

## Purpose

Declares what each gateway in a hierarchy deploys, as application sets of resources and child
gateways with permission-carrying dependencies, and projects the hierarchy as the global manifest.
It also supplies a gateway's deployment of one resource: its instances and the state written to
them. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `IApplication`, `IApplicationBuilder`, and `IApplicationModel` with their
  implementations; the `IApplicationGateway` seam; `IApplicationSet`; `IApplicationResourceDefinition`
  with `DependsOn`, `WithSetting`, `WithExtension`, and its output placeholders; the manifest records; and
  `IApplicationResourceDeployment` and `ApplicationResourceDeployment`.
- Out of scope: gateways, platforms, infrastructure actions, and the deployment manifest's wire
  format (ApplicationModel.Gateway); the identity provider; area verbs such as `AddWeb`.

## Dependencies

`Assimalign.Cohesion.Core` and `Assimalign.Cohesion.ApplicationModel`, the contracts it shares with
running instances.

## Usage

Compose the hierarchy in the root gateway's program, build it, and run it:

```csharp
ApplicationBuilder builder = Application.CreateBuilder("root", args);   // the root set
IApplicationResourceDefinition identity = builder.AddResource("IdentityHub", "identity");

IApplicationSet east = builder.AddGateway("east");
IApplicationResourceDefinition database = east.AddResource("Database.Sql", "orders-db");
IApplicationResourceDefinition ordersApi = east.AddResource("Web", "orders-api")
    .DependsOn(database, "data.read", "data.write")
    .DependsOn(identity)
    .WithSetting("Database:Endpoint", database.Endpoint)        // filled in when east deploys it
    .WithSetting("Identity:Authority", identity.Endpoint)       // passed down by the root gateway
    .WithSetting("Logging:LogLevel:Default", "Information");

// A platform package's verb, here a Kubernetes package's, records how its platform deploys the
// resource; only a gateway on that platform applies it. It calls the generic form:
// WithExtension("kubernetes", "autoscaler", JsonSerializer.SerializeToElement(options, context)).
ordersApi.ConfigureHpa(hpa => hpa.MaxReplicas = 10);

Application application = builder.UseGateway(gateway).Build();   // composition closes here
ApplicationManifest manifest = application.Model.Manifest;       // the global manifest
await application.RunAsync();
```

A gateway that receives a manifest as data, rather than composing one, builds the same model:

```csharp
ApplicationModel model = ApplicationModel.Create(manifestFromArguments);
```

A gateway builds one aggregate per resource it runs, answers each publication, and records each
stateful write:

```csharp
ApplicationResourceDeployment ordersApi = ApplicationResourceDeployment.Create(model.Manifest.Gateways[0].Resources[1]);

// An instance published its manifest: answer with the state it must restore.
IReadOnlyList<ResourceStateSnapshot> restore = ordersApi.Register(published);

// A stateful write succeeded on one instance: restore its snapshot on the others.
foreach (ResourceInstanceKey target in ordersApi.Record(source, write.State!))
{
    // Send write.State to the target instance's control plane (RestoreAsync).
}
```
