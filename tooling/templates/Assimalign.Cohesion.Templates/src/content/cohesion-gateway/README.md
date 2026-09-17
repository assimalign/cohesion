# CohesionProject

Created with `dotnet new cohesion-gateway`. `Program.cs` owns the application entry point.
The SDK supplies .NET 10 executable, language, nullable and AOT defaults.

```bash
dotnet build
dotnet run
```

Set the organization feed owner in `nuget.config` and the image registry in `Directory.Build.props`.
Add enabled resource references to the project before running the gateway. Local runs separate processes; InProcess runs enabled composable members together.
