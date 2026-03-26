## Cohesion SDK

The Cohesion SDK package is built from [Assimalign.Cohesion.Sdk.Tasks.csproj](/C:/Source/repos/assimalign/cohesion/sdks/Assimalign.Cohesion.Sdk/Tasks/Assimalign.Cohesion.Sdk.Tasks.csproj).

### Local Pack Output

Running `dotnet pack` on the SDK project will:

1. Pack the SDK itself into `_out/packages`.
2. Copy the local SDK layout into `_out/dotnet/sdk/<sdk-version>/sdks/Assimalign.Cohesion.Sdk`.

To also attempt a repo-wide local feed build for the library, resource, and `.NET` extension projects, run:

```powershell
dotnet pack sdks\Assimalign.Cohesion.Sdk\Tasks\Assimalign.Cohesion.Sdk.Tasks.csproj -c Debug -p:PackCohesionProjects=true
```

### Example

Add the SDK version to `global.json`:

```json
{
  "msbuild-sdks": {
    "Assimalign.Cohesion.Sdk": "9.0.0"
  }
}
```

Use the SDK in a project file and select a profile with `CohesionSdkType`:

```xml
<Project Sdk="Assimalign.Cohesion.Sdk">
  <PropertyGroup>
    <CohesionSdkType>Web</CohesionSdkType>
  </PropertyGroup>
</Project>
```

Available SDK profiles:

- `Common`
- `Web`
- `Database`
