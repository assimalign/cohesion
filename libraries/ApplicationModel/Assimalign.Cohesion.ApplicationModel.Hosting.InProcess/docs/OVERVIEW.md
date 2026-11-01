# Assimalign.Cohesion.ApplicationModel.Hosting.InProcess — Overview

## Purpose

The instance half of the in-process platform: the gateway client a resource instance uses when its
gateway runs it inside the gateway's own process. The gateway half is
`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess`. The design record is
[DESIGN.md](DESIGN.md).

## Scope

- In scope: `UseInProcess()`, which opts any area's resource into the platform; the client it adds,
  which publishes and renews through the gateway's in-process endpoint and attaches the instance's
  control plane there; `InProcessPlatform`, the platform's name and the process's endpoints; and
  `IInProcessGateway`, the contract the gateway half implements at its endpoint; and
  `InProcessProgramAttribute`, which the source generator applies to a program that references this
  package, naming the entry point it exposes for a gateway that runs the program in process.
- Out of scope: running programs, receiving publications, and calling control planes, which are the
  gateway half's; every other platform.

## Dependencies

`Assimalign.Cohesion.Core`, the shared contracts in `Assimalign.Cohesion.ApplicationModel`, and
`Assimalign.Cohesion.ApplicationModel.Hosting`. It references no gateway, so a resource that opts in
carries neither the gateway nor the logical model.

## Usage

A resource opts in when it is configured, beside any other platform it runs on:

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddResource("orders-api", resource => resource.UseInProcess());
```

`UseInProcess()` extends every area's options (`ApplicationInstanceOptions`) and returns them, so
it chains with an area's own verbs. The instance uses the in-process client only when its gateway
names the `inprocess` platform, which the in-process gateway does for every program it runs, along
with the endpoint the client reaches it at.

An executable that references this package also gets, from the Cohesion source generator, a public
`InProcessEntryPoint.RunAsync(string[] args)` that calls its entry point, top-level statements
included, and an `[assembly: InProcessProgram(...)]` naming it. A gateway program that references
the program's project with `CohesionResource="orders-api"` binds it with no code of its own
(DESIGN H6).

A gateway half registers itself at a unique endpoint of its own and names that endpoint to every
program it starts:

```csharp
Uri endpoint = new($"inprocess://{Guid.NewGuid():N}/");
using IDisposable registration = InProcessPlatform.RegisterGateway(endpoint, gateway);
```
