# Assimalign.Cohesion.Database.Testing — Overview

## Purpose

`Assimalign.Cohesion.Database.Testing` runs a Database program's real entry
point in the test process. The retired resource integration was removed on 2026-10-31 with the
application-model restart (assimalign/cohesion#1759), and the area's resource integration returns
as `Assimalign.Cohesion.Database.Hosting.ApplicationModel`.

## Public surface

- `IDatabaseApplicationTestFactory` — the interface-first lifecycle contract.
- `DatabaseApplicationTestFactory` — the sealed implementation and
  `FromProgram<TProgram>()` entry point.
- `DatabaseApplicationTestFactoryOptions` — entry arguments and the shutdown budget.

The package intentionally carries no xUnit, NUnit, MSTest, or assertion dependency. It hosts
the resource; callers choose their own fixture and assertion conventions.

## Usage

```csharp
await using DatabaseApplicationTestFactory factory =
    DatabaseApplicationTestFactory.FromProgram<global::Program>();

await factory.StartAsync(testCancellation);

await factory.StopAsync(testCancellation);
```

A top-level program adds `public partial class Program { }` so another assembly can use it as
the statically rooted generic marker.

## Relationships

- `Assimalign.Cohesion.Hosting` owns the plain host lifecycle.
- `Assimalign.Cohesion.Database.Hosting` owns `DatabaseApplication.CreateBuilder(args)`.

See [`DESIGN.md`](DESIGN.md) for entry-point and AOT details.

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


## Debugger inspection (#1470)

Eligible concrete source types use private nested debugger views of reviewed public/protected
state; existing inherited display/proxy pairs are reused. See the shared
[debugger convention](../../Assimalign.Cohesion.Database/docs/CONVENTIONS.md#debugger-inspection).
Runtime APIs, methods, allocation budgets, persistent formats and wire behavior are unchanged.

## Packaged TCP composition acceptance (#1391)

The separate `fixtures/Assimalign.Cohesion.Database.TcpSdkHost` executable uses only
`Sdk.Database` and its App/App.Database framework references. Its ordinary Program composes
KeyValuePair, Graph and Blob through `AddServer(server => server.Listen(uri))`, with no direct
project/package references. The compiler-reference artifact must contain Database.Tcp from the
App.Database reference pack and omit Connections.Tcp; the self-contained output must carry the
driver at runtime.

The test launches that process, awaits READY after application startup, completes authentication
and Ping/Pong with each model family, then sends STOP and requires a clean exit. This uses the
existing generic DatabaseClient exchange seam without adding a public Ping member. The 90-second
operation and independent 30-second cleanup budgets are hang guards, not timing assertions.
The fixture disables ApplicationModel/schema generation; the existing SampleHost retains SQL
schema compilation. Both use the fresh local feed from
`scripts/Install-Local.ps1` before the explicit Database.Testing test project is built.

All three fixture engines retain their default in-memory storage: no RootPath or storage
strategy is configured, so the process creates no persistent data directory to clean up.
