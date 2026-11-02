# CohesionProject

Created with `dotnet new cohesion-identityhub`. `Program.cs` owns the application entry point.
The SDK supplies .NET 10 executable, language, nullable and AOT defaults.

```bash
dotnet build
dotnet run
```

Set the organization feed owner in `nuget.config`.

To run the application as a resource that a gateway manages, register it on `builder.Services` with
`builder.Services.AddResource("<name>", resource => resource.UseInProcess())`. The verb comes from
`Assimalign.Cohesion.IdentityHub.Hosting.ApplicationModel`, which the `App.IdentityHub` framework carries; each platform
the resource runs on is a package the project references, such as
`Assimalign.Cohesion.ApplicationModel.Hosting.InProcess` for `UseInProcess()`. When no platform is named in
its environment, the application runs unmanaged and the call registers nothing.
