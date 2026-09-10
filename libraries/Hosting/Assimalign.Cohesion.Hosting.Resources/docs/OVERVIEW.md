# Assimalign.Cohesion.Hosting.Resources

## Summary

Provides Cohesion's opt-in runtime context and supervisor boundary for enabled resource
executables. It composes the plain Hosting run seam with the transport-neutral Health contracts;
neither sibling depends back on Resources.

## Dependencies

| Dependency | Use |
| --- | --- |
| `Assimalign.Cohesion.Core` | Environment contract, paths, URI helpers, and common primitives |
| `Assimalign.Cohesion.Hosting` | Host lifecycle and `IHostRunner`/`IHostRunObserver` seam |
| `Assimalign.Cohesion.Hosting.Health` | Contributor registration and aggregate health reports |
| `System.Security.Cryptography.ProtectedData` | Windows CurrentUser DPAPI mount payloads |

The base `Assimalign.Cohesion.App` framework delivers Resources because every opt-in resource SDK
generates `ResourceRuntime` calls. It delivers Health transitively beside Resources and plain
Hosting. Assembly presence does not activate this runtime; the generated registration exists only
when `CohesionApplicationModel=enabled`. Direct package consumers can still select the packages
they need.

## Primary responsibilities

- `ResourceContext` snapshots identity, environment, content root, endpoints, mounts, settings,
  references, the application trust key, bootstrap credential, and additional frozen contract
  values for one invocation.
- `ResourceRuntime` supplies AsyncLocal scope, assembly-keyed generated registrations, entry
  invocation, and host surrender.
- `IResourceControlPlane` owns host-local contributors, endpoint observations, accepted command
  kinds, health aggregation, and command dispatch.
- `ResourceMount` reads file-backed or in-memory mount values, decrypting Windows DPAPI-backed
  file content for consumers.
- The internal resource runner handles process signals, readiness protocol lines, shutdown budget,
  content-root validation, and stable exit classification.

## Key types

- `ResourceContext`, `ResourceMount`, `ResourceRuntime`, `ResourceEntryExitException`
- `IResourceEntryInvocation`
- `IResourceControlPlane`, `ResourceControlPlane`
- `ResourceCommand`

Health value types are owned by `Assimalign.Cohesion.Hosting.Health`; host lifecycle types are
owned by `Assimalign.Cohesion.Hosting`.

## Invocation model

Generated module initializers register the resource assembly's compiler-rooted entry point and an
area-specific control-plane factory. Each builder obtains a new control plane. When the area builder
calls `ResourceRuntime.HostBuilt`, the runtime observes the invocation endpoints, attaches the host,
installs the resource runner, and completes `IResourceEntryInvocation.HostReady` when applicable.

`ResourceRuntime.InvokeEntry` requires an explicit `CreateScope` frame. It invokes the registered
`Assembly.EntryPoint` on a dedicated invocation thread and returns separate `HostReady` and
`Completion` tasks. This is the one sanctioned reflection operation; there is no assembly scan or
runtime entry-type lookup.

A caller with an independently validated, compiler-rooted executable assembly can query
`ResourceRuntime.IsEntryRegistered` and use `ResourceRuntime.InvokeEntryPoint` as a direct fallback
when generated entry registration is unavailable. The fallback does not weaken `InvokeEntry`'s
registration guard and retains the same explicit-scope, single-invocation, dedicated-thread, and
completion semantics.

## Process protocol

For an ordinary resource process whose started hook completes before a stop is accepted, stdout
transitions are:

1. `cohesion-resource: ready` after the Hosting observer reports `Started`.
2. `cohesion-resource: stopping` when the stop transition is accepted.
3. `cohesion-resource: stopped` after the host finishes its stop sequence.

The host can accept a stop while `OnStartedAsync` is still running. In that race, `ready` is omitted,
the protocol begins with `stopping`, and no late `ready` is emitted.

A failed `ready` write requests shutdown so the host still drains. A failure writing any protocol
line is retained and classified at the executable boundary; `stopping` and `stopped` failures do not
request a shutdown already in progress. In-process entry invocation suppresses stdout protocol,
process-signal subscriptions, named-event waits, and `Environment.ExitCode`; failures instead fault
`IResourceEntryInvocation.Completion` with `ResourceEntryExitException` carrying the classified
exit code.

## Exit mapping

| Code | Meaning |
| ---: | --- |
| 0 | Successful run and drain |
| 64 | Area-classified configuration failure |
| 69 | Area-classified dependency failure |
| 70 | Unclassified failure before ready |
| 75 | Unclassified failure after ready |
| 130 | Drain budget cancelled after SIGINT or the Windows SIGQUIT/CTRL_BREAK mapping |
| 143 | Drain budget cancelled after termination, SIGHUP, named stop event, or another stop request |

Typed area classification checks the currently inspected exception first, so a directly typed outer
exception wins. When that exception is unclassified, classification recurses through its inner
exception or aggregate members; configuration wins over dependency among recursively inspected
aggregate members. Any typed result takes precedence over generic phase/drain classification.
