# CohesionProject

Created with `dotnet new cohesion-database`. `Program.cs` owns the application entry point.
The SDK supplies .NET 10 executable, language, nullable and AOT defaults.

`Program.cs` declares the SQL engine, the `customers` database it owns and that database's schema.
The engine's build creates the database on first start and applies the schema before the server
listens on `cohesion-db://localhost:5740`. Configuration overrides the defaults, for example:

```bash
dotnet build
dotnet run
dotnet run -- --Database:Endpoint=cohesion-db://localhost:5741 --Database:DataPath=./data
```

Set the organization feed owner in `nuget.config` and the image registry in `Directory.Build.props`.
Orchestration starts disabled. Follow the csproj comment to enable the manifest, typed accessors and area-owned default control plane before referencing this project from a gateway.
