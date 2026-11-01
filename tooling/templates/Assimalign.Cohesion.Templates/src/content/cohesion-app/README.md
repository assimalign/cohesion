# `cohesion-app` — one repo, one gateway, an API and a database

`Acme.Api` and `Acme.Database` are ordinary executables with a `Program.cs`. `Acme.Api` makes itself a
managed resource with one call, `builder.Services.AddResource("acme-api", resource => resource.UseInProcess())`:
started by a gateway it publishes itself and the gateway manages it, started on its own it is a plain
Web application. The Database area has no application-model integration yet, so `Acme.Database` always
runs unmanaged; `Acme.Api` reads its endpoint from `Database:Endpoint`.

`Acme.Gateway` declares the model in its `Program.cs`, one `AddResource(provider, name)` per resource:

```csharp
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("acme").UseInProcess(infrastructure);
builder.AddResource("Web", "acme-api").WithSetting("Customers:PageSize", "25");
await builder.Build().RunAsync();
```

Each project's `Properties/launchSettings.json` selects environment `Local` for developer-machine
runs (the gateway's profile sets nothing). Development uses strict deployed security. Use
`dotnet run --no-launch-profile` when selecting an environment through shell variables so launch
settings do not override them.

```bash
dotnet run --project Acme.Database   # the database, unmanaged
dotnet run --project Acme.Api        # the API on its own, unmanaged
dotnet run --project Acme.Gateway    # the gateway and its model
```

## Choosing the platform

A gateway runs its model on one platform, and the platform is a pair of packages: the gateway half,
which `Acme.Gateway` references and registers on its builder, and the instance half, which each
resource references and opts into with `resource => resource.Use<Platform>()`.

- **In process** (this scaffold): `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` in the
  gateway, `Assimalign.Cohesion.ApplicationModel.Hosting.InProcess` in each resource. The gateway runs
  each resource's program on a thread of its own process, so it runs only the programs registered on
  its `InProcessInfrastructure`: reference the resource's project and register its entry point, for
  example `infrastructure.AddProgram("Web", "acme-api", Acme.Api.Program.MainAsync)`, which needs a
  public `MainAsync` in `Acme.Api`. Until a program is registered, deploying its resource fails.
- **Kubernetes**: the platform pair ships from the `cohesion-platforms` repository
  (`Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes` and
  `Assimalign.Cohesion.ApplicationModel.Hosting.Kubernetes`). Replace the in-process reference in
  `Acme.Gateway` with the gateway half and register it on the builder; add the instance half to each
  resource and opt into it beside `UseInProcess()`. The resources' code does not change otherwise.

Set the organization feed owner in `nuget.config`.
