# Assimalign.Cohesion.ApplicationModel.Gateway — DESIGN

> Layer-2a of the ApplicationModel stack: the **control-plane base** plus the default
> **LocalGateway**. The full multi-package architecture (declarative contracts, the
> Kubernetes build-override + self-hosted registry, resource manifest packages) lives in the
> ApplicationModel area-root `../../DESIGN.md`, and the Layer-1 contract library's own design
> record is at `../../Assimalign.Cohesion.ApplicationModel/docs/DESIGN.md`.

## What this library is

This package implements the control-plane contracts defined in
`Assimalign.Cohesion.ApplicationModel`. It contains:

- **`ApplicationGateway`** — the public *guided base* that implements the generic realization
  algorithm once.
- **`InMemoryResourceStateManager`** — the public, race-free reference implementation of
  `IApplicationResourceStateManager` for gateway authors.
- **`LocalGateway`** (+ `LocalGatewayOptions`) — the default gateway for local development,
  which realizes each resource as a supervised child process.
- Internal pieces: `ResourceControlContext`, `LocalResourceResolver`, port and mount
  materializers, the probe runner, `LocalGatewayProcessSupervisor`, `LocalProcessStateStore`,
  `LocalProcessSignal`, `LocalProcessController`, `ExecutableArtifact`.
- **`UseLocalGateway()`** and **`AddExecutable(...)`** builder extensions.

It references the Core-only `Assimalign.Cohesion.ApplicationModel` and
`Assimalign.Cohesion.Security.DataProtection` packages, and is `IsAotCompatible` / AOT-gated —
no reflection, no `Microsoft.Extensions.*`. Publishing the concrete reference
state manager is the signed-off, narrowly scoped exception to the repository's interface-first
default; the interface remains the control-plane contract.

## The generic algorithm (why the base owns it)

`ApplicationGateway` implements `IApplicationGateway` **explicitly** and forwards to
strongly-typed `protected` hooks (`GatherAsync`, `Controllers`, `State`, `StartObserverAsync`),
per the repo's interface-first-with-guided-base convention. Every gateway — local, Docker,
Kubernetes — is the *same* algorithm with different hooks, so it is written once:

1. **Order** `model.Descriptors` topologically (depth-first post-order; the model is already
   validated acyclic at build time, so no cycle guard is needed here).
2. **Gather** each resource's artifact via `GatherAsync` (local → an executable path; container
   gateways → a pre-built image). Gathering locates/validates; it never builds.
3. **Start the single observer** (`StartObserverAsync`) — the only writer of observed status
   into `State`. Controllers only *apply* desired state; they never own steady-state.
4. **Provision in dependency order**: route each resource to the first controller whose
   `CanControl` returns true, `ReconcileAsync` (apply, non-blocking), then
   `State.WaitForStateAsync(id, {Running, Failed, Stopped}, budget)`. `Running` → start
   dependents; `Failed`/`Stopped`/timeout → mark the dependent subtree `Blocked` and throw
   an aggregated error. `Degraded` is observed but never gates: it does not satisfy initial
   readiness, and a later transition to it never re-gates dependents admitted by `Running`.
5. **Teardown** in reverse order (`DeleteAsync`), best-effort. A failure mid-startup triggers
   the same reverse teardown of whatever was already provisioned before the error rethrows.

This is why a `Failed`, cleanly `Stopped`, or never-ready dependency can never deadlock the
graph: the readiness gate is a **terminal-set** membership wait with a budget, not a wait for
one specific state. The single interim static set is named `InitialReadinessTerminals`; item 26
replaces it with the plan-derived gate from O30. This is the cohesion-side contract referenced
by the `cohesion-platforms` rule 2 amendment; that sibling repository is not edited here.

## InMemoryResourceStateManager — the race-free contract

The public reference `IApplicationResourceStateManager` implementation is the load-bearing
correctness piece. Reads, writes, and *waiter registration* all happen under one lock, so the
classic lost-wakeup —
`SetState` firing between a reader observing the current state and subscribing — cannot occur:
`WaitForStateAsync` checks the current state and, if not yet terminal, registers its waiter
**before** releasing the lock. Waiters are completed and the `StateChanged` event is raised
*outside* the lock to avoid re-entrancy. Budget expiry returns the last observed state so callers
learn where a resource got stuck. Caller cancellation instead throws `OperationCanceledException`
and removes the abandoned waiter; terminal completion, timeout, and cancellation all clean up
the registration under the same lock.

## LocalGateway — process realization

- **Resolution**: manifest-backed resources launch the exact `artifact.apphost`; the local
  gateway never falls back to the managed assembly DLL. A plain or orchestration-disabled
  executable can launch only through `AddExecutable(name, path, options)`, with an explicit
  readiness probe or per-resource stdout marker.
- **Endpoints**: each endpoint gets a loopback port persisted in
  `.cohesion/<application>/.state/ports.json`. The gateway injects the frozen `ResourceEnvironment`
  endpoint contract (caller values win), publishes the allocated endpoints atomically with the
  first `Running` transition. Once a manifest-referenced dependency reaches `Running`, the
  dependent receives `COHESION_DEPENDENCY_<RES>_<EP>_{URL,HOST,PORT,SCHEME}` exclusively from
  that dependency's observed endpoints. Optional references never gate startup: they inject only
  when the target is already `Running`, and otherwise inject nothing. An explicit C# `DependsOn`
  edge remains ordering-only.
- **Probes**: HTTP (exactly 200 succeeds; 404/405 fail startup immediately), TCP, and exec are
  gateway-side and AOT-safe. Missing manifest probe roles use the resource's default control-plane
  endpoint and role route (`<path>/readyz` for startup/readiness, `<path>/livez` for liveness);
  explicit `none` disables a role. The canonical
  `cohesion-resource: ready` stdout line starts manifest probing but is not readiness proof.
  `AddExecutable` may instead use its configured marker as the whole readiness signal.
- **Supervision**: stdout and stderr are piped with a `[resource-name]` prefix. A failed liveness
  attempt moves `Running` to `Degraded` with the probe detail. Three consecutive failures restart
  under `OnFailure` (default), `Always`, or `Never`, with 1-second exponential backoff capped at
  30 seconds and five restart attempts. A restart observes
  `Degraded → Stopping → Starting → Running`; dependents are never re-gated.
- **Mounts**: resources receive `COHESION_MOUNT_<M>_PATH` rooted at
  `.cohesion/<application>/<resource>/<mount>`. POSIX directories/files use exact 0700/0600
  modes. A Composite plan's flattened `<member>-<mount>` claim instead receives the outer carrier
  `COHESION_MOUNT_<COMPOSITE>_<MEMBER>_<M>_PATH`, pointing to the same resource-rooted claim;
  inward remapping belongs to `ProcessHost`.
  Windows files are DataProtection ciphertext backed by a CurrentUser DPAPI-protected key
  ring; this makes no ACL claim. Gateway-side source resolution and a distinct child-readable
  Windows delivery carrier remain design item 25 work.
- **Shutdown**: Windows launches use `CreateNewProcessGroup`; POSIX launches use `setsid` when
  the host provides it, with a best-effort `setpgid` fallback. On Windows, a fresh manual-reset
  event named by `COHESION_STOP_EVENT` is primary and targeted `CTRL_BREAK` is the console
  fallback; POSIX sends `SIGTERM` to the process group when isolation succeeded. The gateway
  waits the manifest's `lifecycle.stopGraceSeconds` (30 seconds by default;
  `LocalGatewayOptions.StopGrace` is the
  fallback for opaque executables), then force-kills the whole group/tree. A process that exits
  during grace becomes `Stopped`; one that requires escalation becomes `Failed(forced)`.
- **Known POSIX limitation**: process grouping depends on the host-provided `setsid` command with
  a best-effort `setpgid` fallback; no RID-native launch helper is shipped.
- **Recovery**: `.cohesion/<application>/.state/owner` records the gateway identity and
  `.cohesion/<application>/.state/<resource>/pid` records PID, process start time, executable path,
  process-group ownership, and the Windows stop-event name. A new gateway independently verifies
  PID + start time + executable before adopting a live child and rebuilds readiness from probes.
  `--restart-orphans` (or `LocalGatewayOptions.RestartOrphans`) instead gracefully stops each
  verified child and launches a fresh attempt.

## Testing posture

The generic algorithm and the state manager are unit-tested deterministically (a `TestGateway`
with recording controllers asserts topological start order, reverse-order stop,
failure→`Blocked`+throw, and readiness-timeout; the state manager asserts terminal-set returns
for `Running`/`Failed`/`Stopped`/timeout, cancellation propagation and cleanup, non-gating
`Degraded`, race-free set-before-subscribe, observed endpoints, and the event). The generic
algorithm also verifies that post-`Running` degradation does not re-gate dependents. Real
child-process spawning is exercised by a co-located, BCL-only test apphost. `LocalGateway` tests
cover persisted ports and contract environment, default and explicit HTTP readiness (including
404 fail-fast), TCP and exec probes, liveness degradation/restart/backoff, observed dependency
injection and startup gating, optional absence, Composite re-export names, mount materialization,
prefixed stdout/stderr, `AddExecutable` marker readiness, the full exponential-backoff sequence,
graceful and forced stop classification, PID-file re-attachment, and explicit orphan restart.

## Non-goals

- Building container images or talking to Kubernetes — those are the platform gateway packages
  (`…Gateway.Kubernetes` / `…Gateway.Docker`).
- Owning DI/Config/Logging — that stays inside each `{Resource}.Application` runtime.
- A full drift-reconcile loop with server-side apply and informer resync — that is specified for
  the Kubernetes gateway; the local gateway's supervisor is the equivalent for processes.
