# Assimalign.Cohesion.Database.Testing

Integration testing for SDK-enabled Cohesion database resource programs. The package's
`DatabaseApplicationTestFactory.FromProgram<TProgram>()` entry point invokes the resource's
real `Program.Main` under an isolated `Hosting.Resources` `ResourceContext`, waits for the
generated Database admin plane to report readiness, and requests graceful shutdown through that
same plane.

The resource stays an ordinary top-level `Program.cs`:

```csharp
DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder(args);
builder.AddSql((_, options) =>
{
    options.EngineName = "app-sql";
    options.RootPath = Resource.Mounts.Data.Path;
    options.AddServer(engine => SqlDatabaseServer.Create(
        (SqlDatabaseEngine)engine, new SqlDatabaseServerOptions().Listen(Resource.Endpoints.Db)));
});

builder.AddDatabase("app-sql", "app", SqlSchema.Compile("app", database =>
    database.Table<Account>(table => table.Key(account => account.Id))));

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

## Running this project's tests

**This suite does not run correctly with the commands that work everywhere else in the area.**
Two things differ, and both have cost real debugging time:

**1. Pass the csproj, not the directory.** `tests/` holds two projects — `.Tests.csproj` and the
`.TestHost.csproj` fixture — so `dotnet test <dir>` fails with `MSB1050: Specify which project or
solution file to use`. In a scripted loop that filters output, that error is indistinguishable
from a project that simply has no tests, so the suite silently does not run:

```bash
dotnet test resources/Database/Assimalign.Cohesion.Database.Testing/tests/Assimalign.Cohesion.Database.Testing.Tests.csproj
```

**2. Pack the local feed first.** The acceptance tests build
`fixtures/Assimalign.Cohesion.Database.SampleHost` through the real `Sdk.Database`, which resolves
its SDK and framework packages from `_out/packages`. Without them the fixture cannot build and the
tests fail on missing generated output rather than on anything they are actually asserting:

```bash
pwsh installer/scripts/Install-Local.ps1
```

This is the same command the `database-testing-pack` job runs in
[`.github/workflows/resource-database.yml`](../../../.github/workflows/resource-database.yml)
before the `Database.Testing E2E` matrix. Run it once; it is reused across runs until the packed
versions change.

**Do not write a new bootstrap script for this.** `Install-Local.ps1` already packs the SDK and
framework closure into `_out/packages` and is the documented dev loop in the repo's `CLAUDE.md`.

See [`docs/OVERVIEW.md`](docs/OVERVIEW.md) and [`docs/DESIGN.md`](docs/DESIGN.md).
