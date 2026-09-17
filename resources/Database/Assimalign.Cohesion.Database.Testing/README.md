# Assimalign.Cohesion.Database.Testing

Integration testing for SDK-enabled Cohesion database resource programs. The package's
`DatabaseApplicationTestFactory.FromProgram<TProgram>()` entry point invokes the resource's
real `Program.Main` under an isolated `Hosting.Resources` `ResourceContext`, waits for the
generated Database admin plane to report readiness, and requests graceful shutdown through that
same plane.

The resource stays an ordinary top-level `Program.cs`:

```csharp
DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
await using SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
    options.RootPath = Resource.Mounts.Data.Path);

ISqlSchema declaration = SqlSchema.Create("app", database =>
    database.Table<Account>(table => table.Key(account => account.Id)));
builder.AddDatabase(engine, "app", SqlSchemaCompiler.Compile(declaration));
builder.AddSqlServer(engine, options => options.Listen(Resource.Endpoints.Db));

await using DatabaseApplication application = builder.Build();
await application.RunAsync();

public partial class Program { }
```

The test references that executable and drives the same entry point used out of process:

```csharp
await using DatabaseApplicationTestFactory factory =
    DatabaseApplicationTestFactory.FromProgram<global::Program>();

await factory.StartAsync();
Uri endpoint = factory.ResourceContext.Endpoints["db"];

// Exercise the resource through Database.Client, then stop it gracefully.
await factory.StopAsync();
```

By default, each factory allocates distinct loopback `db` and `admin` endpoints and owns a
temporary `data` directory. Endpoint and reference dictionaries carry validated `System.Uri`
values. Supply `DatabaseApplicationTestFactoryOptions.ResourceContext` (a `Hosting.Resources`
context) when a test needs fixed
endpoints, mounts, settings, or references.

- The package has no assertion helpers and no test-framework dependency.
- Factories use the generated Database control plane at `/readyz` and
  `/cohesion/v1/stop`; they do not add a test-only bootstrap path.
- The compiler-rooted entry point plus the generic preservation annotation make the
  sanctioned `Assembly.EntryPoint` invocation trim/NativeAOT safe.
- No assembly scanning, dynamic loading, or runtime code generation is used.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
