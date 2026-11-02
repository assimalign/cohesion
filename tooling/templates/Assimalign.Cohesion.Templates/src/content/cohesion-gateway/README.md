# CohesionProject

Created with `dotnet new cohesion-gateway`. `Program.cs` owns the gateway's entry point: it names the
gateway (`__APPLICATION_NAME__`, set with `--applicationName`), declares the model and runs it. The SDK
supplies .NET 10 executable, language, nullable and AOT defaults and the
`Assimalign.Cohesion.ApplicationModel.Gateway` package; it generates nothing.

```bash
dotnet build
dotnet run
```

Declare one `builder.AddResource(provider, name)` per resource the gateway runs, for example
`builder.AddResource("Web", "orders-api")`. Each resource is an application that makes itself a resource
with `builder.Services.AddResource("orders-api", resource => resource.UseInProcess())`.

The model runs on one platform, a pair of packages: the gateway half this project references and
registers on the builder, and the instance half each resource opts into. This scaffold uses the
in-process platform (`Assimalign.Cohesion.ApplicationModel.Gateway.InProcess`), which runs each
resource's program in the gateway's own process and therefore runs only the programs registered on
its `InProcessInfrastructure`. The Kubernetes pair ships from the `cohesion-platforms` repository
(`Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes`); reference it in place of the in-process
package and register it on the builder to run the same model in a cluster.

Set the organization feed owner in `nuget.config`.
