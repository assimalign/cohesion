# Assimalign.Cohesion.ApplicationModel.Hosting — Overview

## Purpose

Defines a resource instance and runs its control plane in process: the instance's identity, the
actions it exposes, the state those actions leave, its health contributors, and the manifest it
publishes to the gateway that manages it. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `IApplicationResourceInstance`, actions and their replayable state, health
  contributions, the generic `ResourceControlPlane`, the `ResourceService` that joins it to its host
  and gateway, and the HTTP manifest publisher. The identity value objects, the manifest, invocation
  results, and the publication seam it uses are the shared contracts in
  `Assimalign.Cohesion.ApplicationModel`.
- Out of scope: servers (the Web area serves the control plane over HTTP), gateways and the logical
  model (`Assimalign.Cohesion.ApplicationModel.Definition` and `.Gateway`), identity and credentials.

## Dependencies

`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.Hosting`, and the shared contracts in
`Assimalign.Cohesion.ApplicationModel`. Nothing else, because the library is in the closure of every
resource; the logical model stays out of it.

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
