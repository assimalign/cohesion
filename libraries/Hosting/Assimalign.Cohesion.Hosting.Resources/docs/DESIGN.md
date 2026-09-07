# Assimalign.Cohesion.Hosting.Resources Design

## Design intent

Resources is the opt-in composition layer between a plain Cohesion host and a resource supervisor.
It does not fork Hosting's lifecycle. Instead, it installs an `IHostRunner` on one built host and
observes the public run transitions defined by `Assimalign.Cohesion.Hosting`.

## Dependency direction

The assembly directly references Core, Hosting, and Hosting.Health. Hosting owns lifecycle;
Hosting.Health owns the shared health vocabulary; Resources owns ambient resource state and process
policy. The Windows ProtectedData reference is a BCL facade used only for protected mount files.

This direction keeps plain hosts free of resource environment parsing, signals, process output,
exit codes, and protected mount behavior. It also lets non-host health consumers use the Health
package independently.

The base `Assimalign.Cohesion.App` framework delivers Resources because all opt-in resource SDKs
generate `ResourceRuntime` calls. Resources therefore travels beside the already-shared Hosting
assembly, and Health follows transitively. Delivery is not activation: only an enabled
`CohesionApplicationModel` registration causes an area builder to install resource behavior.

## Ambient invocation context

`ResourceRuntime.Current` returns the `ResourceContext` attached to the current asynchronous flow.
Without an explicit scope it lazily snapshots the frozen `COHESION_*` process environment.
`CreateScope` installs an in-process context and restores the previous frame on ordered disposal,
so parallel invocations do not overwrite process-global state.

Endpoints and references are `System.Uri` values. A declared development port is used only for
standalone execution when no gateway is present. The content root must be absolute and must match
the host environment before the resource run begins.

`ResourceMount` presents one read surface for in-memory and file-backed values. POSIX file mounts
contain plaintext protected by supervisor-enforced file permissions. Windows protected files
contain raw CurrentUser DPAPI ciphertext; read operations return decrypted bytes while the original
path remains available to path-only tools.

## Generated registration and entry invocation

An enabled resource's generated module initializer registers two assembly-keyed facts:

- its compiler-rooted executable entry point; and
- a factory that creates a fresh area control plane with immutable `stopGraceSeconds` metadata.

Registration contains no invocation state. Each builder receives an isolated control plane, adds
host-local health contributors, and calls `ResourceRuntime.HostBuilt`. That method records realized
endpoints, attaches the host, and installs a `ResourceHostRunner` for that host only.

`InvokeEntry` is valid only inside an explicit `CreateScope` and only once per scope frame. The
registered `Assembly.EntryPoint` is invoked on a dedicated thread, and callers receive independent
host-ready and executable-completion tasks. The builder, not the entry-point name, surrenders the
actual built host; generated code therefore need not assume a type named `Program`.

## Control plane

`ResourceControlPlane.Create` returns a fresh control plane configured with area-defined command
kinds. Contributor names and endpoint names are stable keys. A health query snapshots contributors,
runs their asynchronous checks, and returns `ResourceHealthReport` with the least healthy status;
an empty set is healthy. Commands outside the configured kind set are rejected before the attached
host dispatcher is invoked.

The control plane is transport-neutral. Area Hosting packages decide how to expose health and
commands over HTTP or another protocol.

## Resource run modes

`ResourceRuntime.HostBuilt` selects one of two modes:

- **Process:** emit supervisor protocol lines, subscribe to process signals and the Windows named
  stop event, and set `Environment.ExitCode` from the frozen mapping.
- **In process:** selected only for a host surrendered to an active `InvokeEntry`; suppress process
  side effects and let failures fault `IResourceEntryInvocation.Completion`.

Merely creating a resource scope does not select in-process mode. A directly built host continues
to use process behavior.

## Signals and stop acceptance

One process-lifetime BCL router fans SIGINT, SIGTERM, and SIGHUP out to active resource runs; Windows
also uses SIGQUIT for CTRL_BREAK where supported. A Windows resource may additionally wait on the
fresh named event supplied by `COHESION_STOP_EVENT`.

The runner forwards a signal through `IHostRun.TryShutdown`. Hosting accepts it only while that
specific run is `Started`, so pre-start, stopping, stopped, and stale-run signals have no effect.
The first accepted signal is retained for drain-abort classification.

## Stdout protocol

When the started hook completes before a stop is accepted, the process contract is ordered by
Hosting's observer callbacks:

`cohesion-resource: ready` → `cohesion-resource: stopping` → `cohesion-resource: stopped`

`ready` means startup and `OnStartedAsync` completed. `stopping` means the state transition was
accepted and precedes service drain. `stopped` means the stop sequence, host reset, and
`OnStoppedAsync` completed. Each line is written at most once. A failed write is retained; a ready
write failure requests shutdown so teardown still occurs.

Because the host enters `Started` before awaiting `OnStartedAsync`, a stop can be accepted during
that hook. The runner then omits `ready`, begins the protocol with `stopping`, and suppresses the
late `Started` callback after the stop transition.

## Shutdown budget

`stopGraceSeconds` defaults to 30 and must be at least 5. The resource runner sets the host
`ShutdownTimeout` to `max(5 seconds, stopGraceSeconds - 5 seconds)`. Budgets of at least ten
seconds reserve the final five seconds for the supervisor; smaller valid budgets use the five-second
minimum without exceeding the declared grace. Resource hosts cannot independently expand the host
timeout beyond their declared grace. Plain Hosting retains its own default and is unaffected.

## Frozen exit mapping

The process runner converts every boundary result to `cohesion/sysexits/v1`:

| Code | Classification | Supervisor disposition |
| ---: | --- | --- |
| 0 | Success | Completed |
| 64 | Area-classified configuration exception | Final |
| 69 | Area-classified dependency exception | Restartable |
| 70 | Other failure before `ready` | Final |
| 75 | Other failure after `ready` | Restartable |
| 130 | Drain cancellation after interrupt | Interrupted |
| 143 | Drain cancellation after another accepted stop | Terminated |

The area supplies static exception type tests and classification uses no reflection. A direct type
match on the currently inspected exception wins. If it is unclassified, classification recurses
through its inner exception or aggregate members; configuration wins over dependency among the
recursively inspected aggregate members. Any typed result wins over drain/phase fallback. SIGINT
and the Windows SIGQUIT/CTRL_BREAK mapping are interrupts; SIGTERM, SIGHUP, the named event, and an
unclassified requested stop use 143 when their drain budget is cancelled.

## Non-goals

- Resources does not define the host lifecycle or service execution model.
- Resources does not define the health value types.
- Resources does not choose an HTTP or other control-plane transport.
- Resources performs no assembly scanning, dynamic loading, or runtime code generation.
