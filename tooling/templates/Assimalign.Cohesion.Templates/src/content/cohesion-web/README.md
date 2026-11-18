# CohesionProject

Created with `dotnet new cohesion-web`. `Program.cs` owns the application entry point.
The SDK supplies .NET 10 executable, language, nullable and AOT defaults.

```bash
dotnet build
dotnet run
```

`dotnet run` listens on `http://localhost:5000` (loopback only) until the application configures an
endpoint. Set `Http:Endpoints` in `appsettings.json`, for example
`{ "Http": { "Endpoints": { "Main": { "Host": "0.0.0.0", "Port": 8080 } } } }`, or pass
`--Http:Endpoints:Main:Port=8080` on the command line.

Set the organization feed owner in `nuget.config`.
