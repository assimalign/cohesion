# CohesionProject

Created with `dotnet new cohesion-database`. `Program.cs` owns the application entry point.
The SDK supplies .NET 10 executable, language, nullable and AOT defaults.

```bash
dotnet build
dotnet run
```

Set the organization feed owner in `nuget.config` and the image registry in `Directory.Build.props`.
Orchestration starts disabled. Follow the csproj comment to enable the manifest, typed accessors and area-owned default control plane before referencing this project from a gateway.
