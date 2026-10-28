# Assimalign.Cohesion.Hosting.Resources — Overview

## Purpose

Defines a resource instance and runs its control plane in process: the instance's identity, the
actions it exposes, the state those actions leave, its health contributors, and the manifest it
publishes to the gateway that manages it. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: identity value objects, actions and their replayable state, invocation results, the
  manifest, the publication seam, health contributions, the generic `ResourceControlPlane`, and the
  `ResourceService` that joins it to its host and gateway.
- Out of scope: servers (the Web area serves the control plane over HTTP), gateways and application
  resources (`Assimalign.Cohesion.ApplicationModel` and `.Gateway`), identity and credentials.

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

ResourceControlPlane controlPlane = ResourceControlPlane.Create(resource, serializer);

// The resource service publishes the manifest and restores the state written since the last
// deployment before the host starts any service or server, wherever it is registered.
builder.AddService(ResourceService.Create(controlPlane, publisher));

// The area's server, a host service of its own, starts after that and serves the control plane.
builder.AddService(new AreaControlPlaneServer(listener, controlPlane));
```

A server calls `controlPlane.ExecuteAsync(name, ResourceActionContext.Create(operation,
controlPlane.Serializer, input))` for an invocation and `controlPlane.RestoreAsync(snapshot)` when
the gateway replicates a write. In process, a gateway calls the same members on
`IResourceService.ControlPlane` and no server is registered.
