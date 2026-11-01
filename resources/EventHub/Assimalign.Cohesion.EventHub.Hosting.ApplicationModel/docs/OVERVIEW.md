# Assimalign.Cohesion.EventHub.Hosting.ApplicationModel — Overview

## Purpose

Runs a Cohesion EventHub application as a managed resource instance without changing how the
application is composed: through the client of the platform its gateway runs it on, it publishes
its manifest, restores the state the gateway kept, and lets the gateway run the resource's actions.
The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: the `AddResource` verb on `builder.Services`, its `EventHubResourceOptions`, and the
  resource instance it registers for a managed application, with the actions the application adds
  through the base options' `AddAction`.
- Out of scope: every transport, which belongs to the platform packages the resource opts into
  (such as ApplicationModel.Hosting.InProcess); the instance contracts, the control plane, the
  resource service, and action registration (ApplicationModel.Hosting); gateways and the logical
  model (ApplicationModel.Definition and ApplicationModel.Gateway).

## Dependencies

EventHub, EventHub.Hosting, DependencyInjection, Hosting, and ApplicationModel.Hosting.

## Usage

```csharp
EventHubApplicationBuilder builder = EventHubApplication.CreateBuilder(args);
builder.Services.AddResource("events", resource => resource.UseInProcess());   // a resource on the in-process platform

await using EventHubApplication application = builder.Build();
await application.RunAsync();
```

The resource opts into each platform it runs on with that platform's verb. The gateway names the
platform an instance runs on in its environment; without one, the application runs unmanaged and
nothing is registered, and a platform it did not opt into fails its build.

An application adds an action of its own with its JSON context, which declares the action's
content types:

```csharp
builder.Services.AddResource("events", resource => resource
    .UseInProcess()
    .AddAction(services => new PausePartitionAction(services.GetRequiredService<PartitionSet>()))
    .AddAction(new RetentionAction(), EventsJsonContext.Default));
```
