# Assimalign.Cohesion.Database.Testing — Design

## Design intent

A database resource's schema and runtime composition live in its ordinary `Program.cs`.
Integration tests must not reproduce that composition in a callback or a second bootstrap:
doing so can pass while the deployable executable is broken.
`DatabaseApplicationTestFactory.FromProgram<TProgram>()` therefore drives
the executable entry point itself under a test-scoped carrier.

This is the Database equivalent of an application factory, but its boundary differs from
the socketless Web factory. Database wire clients already abstract their transports, while
a Database program also has before-accept provisioning and a long-running host. The factory
preserves those layers.

## Invocation model

The retired resource integration was removed on 2026-10-31 with the application-model restart
(assimalign/cohesion#1759), and the area's resource integration returns as
`Assimalign.Cohesion.Database.Hosting.ApplicationModel`.

`TProgram` is an assembly and entry-point marker, not a composition interface. Requiring an
`IDatabaseApplicationProgram` implementation or a factory delegate would add a test-only API
to every resource and create a second path around top-level statements.

The marker must be the actual declaring type of `Assembly.EntryPoint`. For C# top-level
statements, consumers make the compiler-created type nameable with an empty
`public partial class Program { }` declaration. The entry point may accept no arguments or
one `string[]`, and may return `void`, `int`, `Task`, or `Task<int>`.

## AOT and trimming posture

Top-level C# entry points have compiler-synthesized method names, so reflection-free method
naming is not available. The signed runtime design permits exactly one lookup:
`Assembly.EntryPoint`, on a method the compiler already roots. `FromProgram<TProgram>()` applies the matching
`DynamicallyAccessedMembers` annotation to its statically referenced marker. The small
invocation method carries an explicit trimming suppression documenting why this one lookup
and invoke are safe.

There is no assembly scan, `Assembly.Load*`, string-to-type activation, expression
compilation, reflection-based serialization, or runtime code generation. The testing
package is therefore trim/NativeAOT safe within the deliberately documented entry-point
exception.

## Hosting-isolation exception

Resource-area libraries normally cannot reference their concrete `*.Hosting` module
(`COHRES001`). The area's testing package is the single standing exception because its sole
purpose is to drive that concrete runtime. Its project declares exactly:

```xml
<CohesionHostingIsolationExemptions>Assimalign.Cohesion.Database.Hosting</CohesionHostingIsolationExemptions>
```

The exception does not flow to production libraries. `Database.Testing` is framework-neutral
test infrastructure and the nonpackable SDK sample is consumer evidence; neither moves
or duplicates runtime implementation.

## Non-goals

- Assertions, fixture base classes, and test-framework adapters.
- A second builder/composition callback or resource-specific bootstrap interface.
- Schema compilation or migration policy; the resource's normal builder owns declarations
  and provisioning, and their dedicated work items own compilation.

The fixture declares its relational schema with `Database.Sql.Schema` and, since B1 of the
engine extensibility design, declares its database on the SQL engine builder
(`sql.AddDatabase(name, database => database.Schema(...))`), whose build provisions it inside the
application's Build, before any server accepts. The SDK analyzes that same declaration at build
time and writes `cohesion/database/sample.schema.json` with its hash.

## Phase 29 Database composition migration

Database programs now capture `AddSql(name, sql => ...)` intent (B1 replaced the
phase-29 `AddSql((context, engine) => ...)` shape), register the server through that engine
builder's deferred `AddServer`, and declare the databases the engine provisions on the same
builder. One application Build
constructs and owns the engine and nested server, with every declared database
provisioned; the program disposes the application. The standalone template still
uses its ordinary local data path.
This migration changes composition only; it adds no ApplicationModel declarations,
manifests or resource control planes. Template acceptance explicitly builds all
five emitted Database programs; the Templates workflow runs when Database.Hosting,
Database.Sql, Database.Sql.Schema, Database.Client, Database.Sql.Client or
libraries/Connections change (`tooling-templates.yml`), so an API change those
programs compose reaches template CI.

## Diagnostics

This test harness owns no EventSource. Database.Hosting reports application transitions and
cleanup failures; the root and model sources report the engine work the harness drives.

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
