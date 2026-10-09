# Assimalign.Cohesion.Sdk.Database

`Assimalign.Cohesion.Sdk.Database` layers Database resource defaults and build tooling on the
base Cohesion SDK. A database application keeps each database's schema in C# in `Program.cs`,
declared on the SQL engine builder that provisions it:

```csharp
builder.AddSql("orders-sql", sql =>
{
    sql.AddDatabase("sales", database => database.Schema(schema =>
    {
        schema.Table<Order>("orders", table => table.Key(order => order.Id));
    }));
    sql.AddDatabase(ReportingSchema.Declaration);   // a SqlSchema.Create("reporting", ...) value
});
```

The build never treats `.sql` or another declarative file as a second schema source.

Set `CohesionDatabaseProject` to `true` and select one of the exact model names:

```xml
<PropertyGroup>
  <CohesionDatabaseProject>true</CohesionDatabaseProject>
  <CohesionDatabaseModel>Sql</CohesionDatabaseModel>
</PropertyGroup>
```

`Build` statically analyzes the schema declarations without invoking the application's entry
point. Each database has exactly one declaration: `database.Schema(schema => ...)` made directly on
the parameter of `sql.AddDatabase("<name>", database => ...)`, with an inline lambda, or a
`SqlSchema.Create(...)` or `SqlSchema.Compile(...)` call named for the database. A schema built in
a helper method that receives the `SqlDatabaseBuilder` works at run time but fails the build
(`COHDBSDK102`); declare a reusable schema as a `SqlSchema.Create(...)` value instead. A database
name is unique across every engine of the project, and a schema principal or custom type fails
the build (`COHDBSDK108`), because every SQL engine build refuses it. Every declared database gets
its own canonical schema document,
`$(IntermediateOutputPath)cohesion/database/<database>.schema.json`, and its lowercase SHA-256
hash, `<database>.schema.sha256`, listed in the directory's `schemas.manifest`. The directory can
be overridden with `CohesionDatabaseSchemaOutputDirectory`; the build removes the artifacts of a
database that is no longer declared and touches no other file in it.

Built-in tool sets are `Sql` and `KeyValuePair`. Model names are case-sensitive. SQL migration
generation is explicit and names its database (optional when the project declares exactly one);
each database's migrations live under `Migrations/<database>/`:

```text
dotnet msbuild -t:CohesionDatabaseCreateMigration -p:CohesionDatabaseName=sales -p:CohesionDatabaseMigrationName=add-orders
```

SQL schema compilation uses `Database.Sql.Schema` without loading the SQL engine into MSBuild.
Key-value compilation and migration requests fail explicitly until that model supplies its own
schema contract.
