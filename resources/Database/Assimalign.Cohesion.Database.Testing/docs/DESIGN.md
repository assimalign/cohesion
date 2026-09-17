# Assimalign.Cohesion.Database.Testing — Design

## Design intent

A database resource's schema and runtime composition live in its ordinary `Program.cs`.
Integration tests must not reproduce that composition in a callback or a second bootstrap:
doing so can pass while the deployable executable, generated accessors, or default control
plane is broken. `DatabaseApplicationTestFactory.FromProgram<TProgram>()` therefore drives
the executable entry point itself under a test-scoped carrier.

This is the Database equivalent of an application factory, but its boundary differs from
the socketless Web factory. Database wire clients already abstract their transports, while
an SDK-enabled Database resource also has generated inputs, before-accept provisioning, a
long-running host, and an HTTP admin plane. The factory preserves all of those layers and
uses loopback endpoints by default.

## Invocation model

```text
DatabaseApplicationTestFactory.FromProgram<Program>()
  -> resolve Program's statically referenced assembly and compiler entry point
  -> Hosting.Resources ResourceRuntime.CreateScope(test ResourceContext)
  -> LongRunning Task (captures the scoped ResourceContext)
  -> Assembly.EntryPoint.Invoke(args)
       -> Program.Main
          -> DatabaseApplication.CreateBuilder(args)
          -> generated control plane + ambient endpoints/mounts
          -> Build() / RunAsync()
  -> poll GET admin /readyz while Program.Main remains live
```

`TProgram` is an assembly and entry-point marker, not a composition interface. Requiring an
`IDatabaseApplicationProgram` implementation or a factory delegate would add a test-only API
to every resource and create a second path around top-level statements. Generated code
registers an enabled resource's control-plane factory with `ResourceRuntime`; the testing
package invokes the compiler-rooted entry while that `Hosting.Resources` seam carries the context.

The marker must be the actual declaring type of `Assembly.EntryPoint`. For C# top-level
statements, consumers make the compiler-created type nameable with an empty
`public partial class Program { }` declaration. The entry point may accept no arguments or
one `string[]`, and may return `void`, `int`, `Task`, or `Task<int>`.

## Lifecycle contract

| Phase | Behavior |
| --- | --- |
| `FromProgram` | Validates the marker and options; creates or adopts a `Hosting.Resources` `ResourceContext`. Nothing runs. |
| `StartAsync` | Starts one long-running entry invocation inside the scoped context, then polls `GET /readyz` until healthy or the startup budget expires. Repeated calls after readiness are no-ops. |
| Running | The `Hosting.Resources` `ResourceRuntime` keeps the context isolated in that invocation's async flow. The test reaches SQL through `db` and management through `admin`. |
| `StopAsync` | Posts `/cohesion/v1/stop`, waits for `Program.Main` to complete, and clears the invocation so a deliberate restart can use a fresh scope. A never-started stop is a no-op. |
| `DisposeAsync` | Bounds graceful stop with `ShutdownTimeout`, then best-effort removes a factory-owned temporary data mount. Custom mounts remain caller-owned. |

The control plane is the source of truth for readiness and shutdown. The factory neither
cancels an unrelated task nor disposes engines behind the composition root's back. Program
scope disposal remains responsible for application and engine disposal after graceful stop.

## Isolation and parallelism

Each default factory owns unique loopback ports and a unique data directory. The
The `Hosting.Resources` `ResourceContext` travels through `ResourceRuntime.CreateScope`, which is
asynchronous-flow local; the factory never edits `COHESION_*` process variables. Generated
registrations are
assembly keyed and create a new control-plane instance for each builder. Consequently,
multiple factories can run concurrently when their supplied contexts do not reuse ports or
mounts.

Port reservation and later binding are distinct operating-system operations, so a hostile
external process can still claim a released ephemeral port. Tests needing absolute endpoint
coordination should supply a context with ports reserved by their own harness.

## AOT and trimming posture

Top-level C# entry points have compiler-synthesized method names, so reflection-free method
naming is not available. The signed runtime design permits exactly one lookup:
`Assembly.EntryPoint`, on a method the compiler already roots. The enabled resource's
generated module initializer applies `DynamicDependency` to public and non-public `Program`
methods and registers the assembly entry. `FromProgram<TProgram>()` applies the matching
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
test infrastructure and the nonpackable SDK sample is consumer/E2E evidence; neither moves
or duplicates runtime implementation.

## Real-process verification

The package's own tests also build
`<repo>/samples/Assimalign.Cohesion.Database.SampleHost` through the real
`Assimalign.Cohesion.Sdk.Database` with `CohesionApplicationModel=enabled`. The test reference
uses `ReferenceOutputAssembly=false`, loads `obj/.../cohesion/resource.json`, and asks
`LocalGateway` to launch the manifest's apphost. The test reads the gateway's stable
`&lt;state&gt;/&lt;application&gt;/.state/ports.json` carrier to address the realized endpoints;
it does not depend on an application-export API. Coverage asserts:

- Database manifest defaults and generated `Resource`/control-plane source;
- real-process readiness, liveness, health, endpoint discovery, command discovery, and
  graceful stop on the `admin` endpoint;
- the advertised `database.add-database` and `database.add-principal` command kinds; the latter
  returns a named runtime refusal until a principal mutation seam exists;
- typed SQL client round-trips on the realized `db` endpoint; and
- recovery of committed rows after a graceful gateway stop and relaunch over the same
  materialized volume.

## Non-goals

- Assertions, fixture base classes, and test-framework adapters.
- A second builder/composition callback or resource-specific bootstrap interface.
- Schema compilation or migration policy; the resource's normal builder owns declarations
  and provisioning, and their dedicated work items own compilation.
- Replacing real-process gateway tests. The in-process factory gives fast resource tests;
  the sample E2E independently validates the SDK manifest and process carrier.

## Bootstrap identity (O35)

Default program factories issue an ephemeral ES256 JWT with issuer `tests`, subject `inprocess`,
and the program assembly name as audience. The ambient context carries the token and its public
P-256 trust JWK. Internal stop requests send that JWT as Bearer; public clients remain uncredentialed.
Custom managed contexts must supply a matching JWT and public trust key.
