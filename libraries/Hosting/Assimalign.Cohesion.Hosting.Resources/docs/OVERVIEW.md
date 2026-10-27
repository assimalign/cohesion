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
    AreaJsonContext.Default.LogLevelSetting,     // source-generated, so it is NativeAOT-safe
    setting => logging.SetMinimumLevel(setting.Level));

ResourceControlPlane controlPlane = ResourceControlPlane.Create(resource, publisher);

// Register it before the services that serve traffic: the host starts it first, and it
// restores the state written since the last deployment before they start.
builder.AddService(controlPlane);
```

A transport then calls `controlPlane.ExecuteAsync(name, ResourceActionContext.Create(...))` for an
invocation and `controlPlane.RestoreAsync(snapshot)` when the gateway replicates a write.
