# CohesionProject

Created with `dotnet new cohesion-gateway`. `Program.cs` owns the application entry point.
The SDK supplies .NET 10 executable, language, nullable and AOT defaults.

```bash
dotnet build
dotnet run
```

Set the organization feed owner in `nuget.config` and the image registry in `Directory.Build.props`.
Add enabled resource references to the project before running the gateway. With no environment
or gateway selected, the apphost uses Local and the first declared gateway, InProcess, to run
enabled composable members together. `--gateway local` runs separate processes. Explicit deployed
environments require `--gateway` or `COHESION_GATEWAY`; the launch profile preserves those values.
