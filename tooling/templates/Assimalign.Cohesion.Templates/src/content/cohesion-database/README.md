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

Set the organization feed owner in `nuget.config`.

The Database area has no application-model integration yet, so a gateway cannot manage this application; it
runs as a plain executable.
