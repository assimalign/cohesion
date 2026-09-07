# Assimalign.Cohesion.Database.Testing — Overview

## Purpose

`Assimalign.Cohesion.Database.Testing` runs an SDK-enabled Database resource's real entry
point in the test process. It installs a test-owned `ResourceContext`, invokes
`Program.Main`, waits until the Database default control plane reports ready, and shuts the
resource down through its public graceful-stop endpoint. A test therefore exercises the same
composition in `Program.cs`—engine, schema/provisioning, servers, generated control plane,
and host lifecycle—that a gateway launches in another process or container.

## Public surface

- `IDatabaseApplicationTestFactory` — the interface-first lifecycle and context contract.
- `DatabaseApplicationTestFactory` — the sealed implementation and
  `FromProgram<TProgram>()` entry point.
- `DatabaseApplicationTestFactoryOptions` — custom context, entry arguments, startup and
  shutdown budgets, and probe cadence.

The package intentionally carries no xUnit, NUnit, MSTest, or assertion dependency. It hosts
the resource; callers choose their own fixture and assertion conventions.

## Default context

When no context is supplied, a factory creates:

| Input | Default test value |
| --- | --- |
| Application | `tests` |
| Environment | `Testing` |
| Gateway | `inprocess` |
| `db` endpoint | Unique loopback port, `cohesion-db` scheme |
| `admin` endpoint | Unique loopback port, `http` scheme |
| `data` mount | Unique temporary directory, removed on disposal |

The loopback ports are allocated independently per factory, and the context is carried by
`ResourceRuntime`'s asynchronous invocation scope rather than process environment variables.
Tests that need deterministic ports or additional generated inputs can pass a complete
`ResourceContext` through the options object. A custom context must include the `admin`
endpoint because readiness and graceful stop are part of the factory contract.

## Usage

```csharp
await using DatabaseApplicationTestFactory factory =
    DatabaseApplicationTestFactory.FromProgram<global::Program>(
        new DatabaseApplicationTestFactoryOptions
        {
            StartupTimeout = TimeSpan.FromSeconds(20),
        });

await factory.StartAsync(testCancellation);

Uri database = factory.ResourceContext.Endpoints["db"];
DatabaseConnectionSettings settings = DatabaseConnectionSettings.For(
    database,
    database: "app",
    principal: "integration-test");

await factory.StopAsync(testCancellation);
```

The context exposes endpoints and references as validated `System.Uri` values. Pass those values
directly to `DatabaseConnectionSettings.For(Uri)` for clients or
`SqlDatabaseServerOptions.Listen(Uri)` for server binding; neither path requires string
reconstruction.

The executable project must have `CohesionApplicationModel=enabled` so its SDK-generated
module initializer registers the Database default control plane. A
top-level program adds `public partial class Program { }` so another assembly can use it as
the statically rooted generic marker.

## Relationships

- `Assimalign.Cohesion.Hosting` owns `ResourceContext`, the scoped runtime carrier, and the
  host/control-plane bridge.
- `Assimalign.Cohesion.Database.Hosting` owns `DatabaseApplication.CreateBuilder(args)` and
  serves the private admin endpoint the factory probes.
- `Assimalign.Cohesion.Database.ApplicationModel` is injected into enabled resource
  executables by `Sdk.Database`; its generated registration supplies the Database default
  control plane. It is not a dependency of this testing library.

See [`DESIGN.md`](DESIGN.md) for lifecycle, entry-point, isolation, and AOT details.
