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

To run the application as a resource that a gateway manages, register it on `builder.Services` with
`builder.Services.AddResource("<name>", resource => resource.UseInProcess())`. The verb comes from
`Assimalign.Cohesion.Web.Hosting.ApplicationModel`, which the `App.Web` framework carries; each platform
the resource runs on is a package the project references, such as
`Assimalign.Cohesion.ApplicationModel.Hosting.InProcess` for `UseInProcess()`. When no platform is named in
its environment, the application runs unmanaged and the call registers nothing.
