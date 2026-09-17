# Assimalign.Cohesion.Sdk.Database

`Assimalign.Cohesion.Sdk.Database` layers Database resource defaults and build tooling on the
base Cohesion SDK. A database application keeps its schema in the C# `SqlSchema.Create(...)` calls
in `Program.cs`; the build never treats `.sql` or another declarative file as a second schema
source.

Set `CohesionDatabaseProject` to `true` and select one of the exact model names:

```xml
<PropertyGroup>
  <CohesionDatabaseProject>true</CohesionDatabaseProject>
  <CohesionDatabaseModel>Sql</CohesionDatabaseModel>
</PropertyGroup>
```

`Build` statically analyzes the schema declarations without invoking the application's entry
point. The current artifact contract requires exactly one `SqlSchema.Create(...)` declaration; split
multiple logical databases into separate SDK artifacts. It writes the canonical schema document to
`$(IntermediateOutputPath)cohesion/database.schema.json` and its lowercase SHA-256 hash to
`database.schema.sha256`. Both paths can be overridden with
`CohesionDatabaseSchemaOutputPath` and `CohesionDatabaseSchemaHashOutputPath`.

Built-in tool sets are `Sql` and `KeyValuePair`. Model names are case-sensitive. SQL migration
generation is explicit:

```text
dotnet msbuild -t:CohesionDatabaseCreateMigration -p:CohesionDatabaseMigrationName=add-orders
```

SQL schema compilation uses `Database.Sql.Schema` without loading the SQL engine into MSBuild.
Key-value compilation and migration requests fail explicitly until that model supplies its own
schema contract.
