# Assimalign.Cohesion.Sdk.Gateway

## Summary

`Assimalign.Cohesion.Sdk.Gateway` is the MSBuild SDK for a Cohesion application gateway.
It chains to the base `Assimalign.Cohesion.Sdk`, defaults the project to an executable, and
references the `Assimalign.Cohesion.ApplicationModel.Gateway` package at the SDK's own
version. It adds no framework and generates no model; on a project reference it reads only
`CohesionResource`, which binds an in-process resource's program. The design record is
[DESIGN.md](DESIGN.md).

## Project contract

```xml
<Project Sdk="Assimalign.Cohesion.Sdk.Gateway">
  <ItemGroup>
    <!-- The platform the model runs on. -->
    <CohesionPackageReference Include="Assimalign.Cohesion.ApplicationModel.Gateway.InProcess" Version="$(CohesionVersion)" />
  </ItemGroup>
  <ItemGroup>
    <!-- The resource program the in-process platform runs, and the resource it runs. -->
    <ProjectReference Include="..\Orders.Api\Orders.Api.csproj" CohesionResource="orders-api" />
  </ItemGroup>
</Project>
```

The gateway's `Program.cs` declares its model and runs it on the platform it references:

```csharp
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("orders").UseInProcess();
builder.AddResource("Web", "orders-api");

await using ApplicationGateway gateway = builder.Build();
await gateway.RunAsync();
```

The in-process platform runs each resource's program in the gateway's own process, so the
gateway references the programs it runs. `CohesionResource` on the reference names the resource;
the build binds the program's entry point, top-level statements included, so the gateway calls
no `AddProgram`. `CohesionProvider` on the reference overrides the provider the program
recorded, for a program that references no area integration or several. An out-of-process platform, such as the
Kubernetes pair in the `cohesion-platforms` repository, replaces the in-process package and
its registration; the model does not change.

## Properties

| Property | Default | Effect |
| --- | --- | --- |
| `OutputType` | `Exe` | Set when empty, before `Microsoft.NET.Sdk` defaults it to `Library`. |
| `SelfContained` | `true` (executables) | Set when empty, so a gateway that references a self-contained `Sdk.<Area>` resource project builds and starts. |
| `RuntimeIdentifier` | `$(NETCoreSdkRuntimeIdentifier)` | Set when empty and self-contained. |
| `ValidateExecutableReferencesMatchSelfContained` | `false` | Set when empty, as for resource executables. |
| `CohesionGatewayImplicitReference` | (empty) | `false` removes the implicit `Assimalign.Cohesion.ApplicationModel.Gateway` reference. |

Gateway inherits the base SDK's [project defaults](../../../Assimalign.Cohesion.Sdk/Tasks/docs/OVERVIEW.md#project-defaults):
`net10.0`, preview language and features, disabled implicit usings, enabled nullable
analysis, and AOT compatibility.

## Consumption

Pin the SDK and the base SDK at one version in `global.json`:

```json
{
    "msbuild-sdks": {
        "Assimalign.Cohesion.Sdk":         "10.0.0-preview.1",
        "Assimalign.Cohesion.Sdk.Gateway": "10.0.0-preview.1"
    }
}
```
