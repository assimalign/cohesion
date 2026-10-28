# Assimalign.Cohesion.Hosting.Resources — Overview

## Purpose

Defines a resource instance and runs its control plane in process: the instance's identity, the
actions it exposes, the state those actions leave, its health contributors, and the manifest it
publishes to the gateway that manages it. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: identity value objects, actions and their replayable state, invocation results, the
  manifest, the publication seam, health contributions, and the generic `ResourceControlPlane`.
- Out of scope: transports (the Web area binds HTTP), gateways and application resources
  (`Assimalign.Cohesion.ApplicationModel` and `.Gateway`), identity and credentials.

## Dependencies

`Assimalign.Cohesion.Core` and `Assimalign.Cohesion.Hosting`. Nothing else, because the library is in
the closure of every resource and every gateway.

## Usage

An area defines its actions and hands the instance to a control plane:

```csharp
// A stateful control: a read reports the level, a write sets it.
ResourceActionState<LogLevelSetting> state = ResourceActionState<LogLevelSetting>.Create(
    new LogLevelSetting("Information"),          // the level the application's code configured
    setting => logging.SetMinimumLevel(setting.Level));

// The format of everything the instance exchanges: JSON over source-generated contexts by
// default, so it is NativeAOT-safe. A platform may bring its own serializer instead.
IResourceContentSerializer serializer = JsonResourceContentSerializer.Create(AreaJsonContext.Default);

ResourceManifestPublisher publisher = ResourceManifestPublisher.Create(new ResourceManifestPublisherOptions
{
    Endpoint = manifestEndpoint,                          // COHESION_MANIFEST_ENDPOINT
    HttpClientFactory = () => clients.Create("gateway"), // optional: pooled handlers
    Serializer = serializer,
});

ResourceControlPlane controlPlane = ResourceControlPlane.Create(resource, publisher, serializer);

// Wrap it in the area's IResourceService and register that before the services that serve
// traffic: the host starts it first, and it restores the state written since the last
// deployment before they start.
builder.AddService(new AreaResourceService(controlPlane));
```

A transport then calls `controlPlane.ExecuteAsync(name, ResourceActionContext.Create(operation,
controlPlane.Serializer, input))` for an invocation and `controlPlane.RestoreAsync(snapshot)` when
the gateway replicates a write.
