# Assimalign.Cohesion.Database.Testing — Overview

## Purpose

`Assimalign.Cohesion.Database.Testing` runs an SDK-enabled Database resource's real entry
point in the test process. It installs a test-owned `Hosting.Resources` `ResourceContext`, invokes
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
the `Hosting.Resources` `ResourceRuntime` asynchronous invocation scope rather than process
environment variables.
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

The real-process acceptance harness verifies discovery of the two Database command kinds,
`database.add-database` and `database.add-principal`, alongside the existing readiness and
graceful-stop routes. Principal mutation currently returns an explicit provider refusal.

## Relationships

- `Assimalign.Cohesion.Hosting` owns the plain host lifecycle.
- `Assimalign.Cohesion.Hosting.Resources` owns `ResourceContext`, the scoped runtime carrier,
  resource entry registration, and the host/control-plane bridge.
- `Assimalign.Cohesion.Database.Hosting` owns `DatabaseApplication.CreateBuilder(args)` and
  serves the private admin endpoint the factory probes.
- `Assimalign.Cohesion.Database.ApplicationModel` is injected into enabled resource
  executables by `Sdk.Database`; its generated registration supplies the Database default
  control plane. It is not a dependency of this testing library.

See [`DESIGN.md`](DESIGN.md) for lifecycle, entry-point, isolation, and AOT details.

## Error and construction conventions (#1470)

Exception messages and format templates are generated from `src/Properties/ErrorMessages.resx`;
format arguments use the current culture. Cancellation and abort templates used during normal
cleanup are cached before reuse. Error identities remain separate from message text where a
structured diagnostic carrier exists. Standard .NET guards retain their argument names and exception types.


## Shared TCP binding in the SDK fixture (found during #1470)

The real SampleHost imports `Assimalign.Cohesion.Database` for the generic `Listen(Uri)`
extension supplied by Database.Tcp. Its SQL namespace import supplies model APIs; it does not
bring the shared server-options extension into scope. This fixture is compiled against the
installed SDK/framework packs after Install-Local, so it verifies the same exported binding an
application consumes. Retained outputs compiled against the former Sql.Tcp namespace must not
be used as evidence that a fresh compile succeeds. No fixture-specific listener or alternate
bootstrap replaces the resource's ordinary Program entry point.


## Workflow hang guards (#1470)

The Database workflow keeps a 60-minute build-job timeout and an explicit 45-minute E2E-job
timeout. The three latest successful consolidated main runs (39656175267, 39625288435 and
39285511469) measured at most 24m 1s for a build leg and 3m 15s for an E2E leg. Twice each
maximum rounded up to 15 minutes gives 60 and 15 minutes respectively; the issue's 45-minute
E2E minimum governs the latter. These job timeouts and the fixture's existing two-minute test
budget are hang guards. The shared build action retains blame-hang diagnostics and mini-dumps.


## Checked manifest paths and sources (#1470)

Manifest probe `Http` is `ResourceHttpPath?`, control-plane `Path` is `ResourceHttpPath`, and mount
`Source` is `ResourceMountSource?`. Consumers use the underlying `Value` when composing addresses
or comparing raw text. Valid string initializers remain supported; malformed strings are refused
at construction or typed JSON import. Default structs still pass through explicit manifest/plan
validation. JSON stays plain strings/nulls under `cohesion/resource/v1`; no transport message,
Database storage format, diagnostic identity, event or counter changes. All binary consumers
must be rebuilt together, including installed SDK/framework E2E payloads.

The SampleHost inherits equivalent compiler and AOT defaults from the shared build. Manifest generation, its local framework consumer and all end-to-end host behavior are unchanged.


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Views return the stored resource context without starting a host or reading synchronized lifecycle state; bootstrap credential state remains hidden.
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.
