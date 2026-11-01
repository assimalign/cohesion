# Assimalign.Cohesion.Sdk.Database

`Assimalign.Cohesion.Sdk.Database` layers Database resource defaults and build tooling on the
base Cohesion SDK. A database application keeps each database's schema in C# in `Program.cs`,
declared on the SQL engine builder that provisions it:

```csharp
builder.AddSql("orders-sql", sql =>
{
    sql.AddDatabase("sales", database => database.Schema(schema =>
    {
        schema.Table<Order>("orders", table =>
        {
            table.Key(order => order.Id);
            table.Default(order => order.Status, "pending");
            table.Default(order => order.Quantity, 7);
            table.Default(order => order.Active, true);
            table.DefaultSql(order => order.Placed, "'2026-01-01'");
        });
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
name is unique across every engine of the project, and a schema principal fails
the build (`COHDBSDK108`), because every SQL engine build refuses it. A table's
`table.Check("ck_name", "LENGTH(Name) > 0")` is read as two string constants; the SDK does not
parse the SQL and does not see the engine's registered functions, so a CHECK that calls one
compiles here and the engine's build validates it before touching a file. Every declared database gets
its own canonical schema document,
`$(IntermediateOutputPath)cohesion/database/<database>.schema.json`, and its lowercase SHA-256
hash, `<database>.schema.sha256`, listed in the directory's `schemas.manifest`. The directory can
be overridden with `CohesionDatabaseSchemaOutputDirectory`; the build removes the artifacts of a
database that is no longer declared and touches no other file in it.

`Default` takes a typed C# value and handles SQL quoting; `DefaultSql` accepts one SQL literal
as written. The SDK accepts compile-time constants for typed defaults, including enum members,
characters, and `null`. A constructor, method call, or other nonconstant value fails with
`COHDBSDK109` and points to `DefaultSql`. Runtime and SDK use the same literal renderer, and a
typed value and its exact `DefaultSql` literal have identical v11 documents and hashes.

Built-in tool sets are `Sql` and `KeyValuePair`. Model names are case-sensitive. SQL migration
generation is explicit and names its database (optional when the project declares exactly one);
each database's migrations live under `Migrations/<database>/`:

```text
dotnet msbuild -t:CohesionDatabaseCreateMigration -p:CohesionDatabaseName=sales -p:CohesionDatabaseMigrationName=add-orders
```

SQL schema compilation uses the `cohesion/database-schema/v11` contract in `Database.Sql.Schema`
without loading the SQL engine into MSBuild. Migration generation uses `Database.Sql.Language`
to validate and render DEFAULT literals, and supports required column additions with non-null
literal defaults, matching runtime migrations.
Key-value compilation and migration requests fail explicitly until that model supplies its own
schema contract.

Declared scripts (`database.Script(name, sql)`) record a receipt per statement at run time, which
generated migration files cannot, so a project with `CohesionDatabaseProject` set refuses them with
`COHDBSDK110`. To use scripts with SDK migrations, declare them in a separate executable that
declares the same database and runs once in Apply mode during deployment, then start the
application in Verify mode ([DESIGN.md](DESIGN.md#e4-script-artifact-decision);
[#1504](https://github.com/assimalign/cohesion/issues/1504) tracks script-aware migrations).


## Shared defaults and diagnostic resources (#1470)

The Defaults shared module resolves incompatible-value text through a consuming-assembly-local
`SqlDefaultLiteralErrorMessages` binding to the generated internal resource accessor. SQL schema
and SDK extraction therefore retain identical ArgumentException text and the `value` parameter,
without a resource lookup on successful rendering. SQL explanatory message fragments also use
resources. SDK compiler identifiers remain `COHDBSDK`; embedded runtime identities use `CK00001`
for language syntax and `CS03101` for unsupported principal declarations. Schema format remains
`cohesion/database-schema/v11`; SQLSTATE, wire, diagnostics carriers and public APIs are unchanged.


The shared Types module also binds modifier-refusal templates to each consumer's own generated
resources through internal `SqlTypeModifierErrorMessages`. Unsupported type/modifier text and
codes are preserved. Numeric modifier spelling remains invariant SQL text; message templates
format with the current culture. Successful type validation adds no resource lookup.

## Rules audit after main cleanup

The migration-ordinal refusal uses the generated ErrorMessages accessor with its existing text
and COHDBSDK206 reporting boundary. Assembly-local Defaults/Types bindings live under Internal
and retain the linked owning namespaces. Private fixture constants use repository casing;
compiler identifiers, generated artifact contents, schema format and extraction behavior are unchanged.

SDK defaults and migration bookkeeping use separate item groups for each item type. This organization preserves the resource metadata and generated schema/migration artifacts.

## Error message producers and catalog carriers

Migration error factories and their refusal reasons, portable type-identity refusals, and
compiled-schema source-writer refusals use generated ErrorMessages resources. Their original
exception types/text and outer COHDBSDK compiler identities remain unchanged. Generated SQL,
portable metadata identities and other artifact content keep their invariant spellings.
