# Assimalign.Cohesion.ApplicationModel.Hosting — Overview

## Purpose

Defines a resource instance and the abstractions for hosting it: the instance's identity, the actions
it exposes, the state those actions leave, its health contributors, the control plane that runs
them, and the host service that joins it to its gateway through a platform's client. The design
record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `IApplicationResourceInstance`, actions and their replayable state, health
  contributions, the generic `ApplicationInstanceControlPlane`, the `ApplicationInstanceService`
  that joins it to its host and gateway, the instance half's seam `IApplicationGatewayClient`, and
  `ApplicationInstanceOptions`, the base of every area's resource options through which a resource
  opts into each platform it runs on. The identity value objects, the manifest, invocation results,
  the content serializer, and the publication seam are the shared contracts in
  `Assimalign.Cohesion.ApplicationModel`.
- Out of scope: every transport. A platform's instance half implements the client, such as
  `Assimalign.Cohesion.ApplicationModel.Hosting.InProcess`; gateways and the logical model
  (`Assimalign.Cohesion.ApplicationModel.Definition` and `.Gateway`); identity and credentials.

## Dependencies

`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.Hosting`, and the shared contracts in
`Assimalign.Cohesion.ApplicationModel`. Nothing else, because the library is in the closure of every
resource; the logical model stays out of it.

## Usage

An area's resource options derive from the shared base, so a platform's verb works on them:

```csharp
public sealed class AreaResourceOptions : ApplicationInstanceOptions
{
    // The area's own settings, such as the actions an application adds.
}

// In the application's Program.cs: the resource opts into every platform it runs on.
builder.AddResource("orders-api", resource => resource.UseInProcess());
```

The area defines its actions, hands the instance to a control plane, and registers the service with
the client the options select:

```csharp
// A stateful control: a read reports the level, a write sets it.
ApplicationActionState<LogLevelSetting> state = ApplicationActionState<LogLevelSetting>.Create(
    new LogLevelSetting("Information"),          // the level the application's code configured
    setting => logging.SetMinimumLevel(setting.Level));

// No platform named: the application runs unmanaged.
if (options.GetGatewayClient() is not { } client)
{
    return builder;
}

// The format of everything the instance exchanges: JSON over source-generated contexts by
// default, so it is NativeAOT-safe.
IApplicationContentSerializer serializer = JsonApplicationContentSerializer.Create(AreaJsonContext.Default);
ApplicationInstanceControlPlane controlPlane = ApplicationInstanceControlPlane.Create(resource, serializer);

// The service publishes the manifest and restores the state written since the last deployment
// before the host starts any service or server, then opens the control plane to the gateway's
// calls through the client.
builder.AddService(ApplicationInstanceService.Create(controlPlane, client));
```

The area serves nothing itself: the platform's client calls `controlPlane.ExecuteAsync(name,
ApplicationActionContext.Create(operation, controlPlane.Serializer, input))` for an invocation and
`controlPlane.RestoreAsync(snapshot)` when the gateway replicates a write.
