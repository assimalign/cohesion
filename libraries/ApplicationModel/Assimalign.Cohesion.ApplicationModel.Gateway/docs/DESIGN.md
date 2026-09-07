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
- Internal pieces: `ResourceControlContext`, `LocalResourceResolver`,
  `LocalGatewayProcessSupervisor`, `LocalProcessController`, `ExecutableArtifact`.
- **`UseLocalGateway()`** builder extensions.

It references `Assimalign.Cohesion.ApplicationModel` only, and is `IsAotCompatible` /
AOT-gated — no reflection, no `Microsoft.Extensions.*`. Publishing the concrete reference
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

- **Resolution**: `LocalResourceResolver` maps an `IExecutableResource.Artifact` to
  `{artifact}.exe` (Windows) or `{artifact}` (elsewhere) next to the orchestrator's
  `AppContext.BaseDirectory`. A `dotnet run`-against-project dev fallback is a planned follow-up.
- **Supervision**: `LocalGatewayProcessSupervisor` is the gateway's single state writer. It
  spawns the child with redirected output (piped with a `[resource-name]` prefix), marks it
  `Starting`, then `Running` on a readiness signal — a configurable stdout marker, or (default)
  surviving a short settle window. Process exit maps to `Stopped` (exit 0) or `Failed`; either
  state observed before `Running` fails initial readiness immediately.
- **Shutdown**: reverse-order `Kill(entireProcessTree: true)` bounded by `StopGrace`, marking
  `Stopped`. A graceful, platform-specific `SIGTERM` / console-Ctrl-C ahead of the kill is a
  planned follow-up (there is no cross-platform BCL primitive for it).

## Testing posture

The generic algorithm and the state manager are unit-tested deterministically (a `TestGateway`
with recording controllers asserts topological start order, reverse-order stop,
failure→`Blocked`+throw, and readiness-timeout; the state manager asserts terminal-set returns
for `Running`/`Failed`/`Stopped`/timeout, cancellation propagation and cleanup, non-gating
`Degraded`, race-free set-before-subscribe, observed endpoints, and the event). The generic
algorithm also verifies that post-`Running` degradation does not re-gate dependents. Real
child-process spawning is exercised end-to-end by the Scheduler proof (Phase 3);
`LocalGateway`'s own tests cover its identity, wiring, and the artifact-resolution error path.

## Non-goals

- Building container images or talking to Kubernetes — those are the platform gateway packages
  (`…Gateway.Kubernetes` / `…Gateway.Docker`).
- Owning DI/Config/Logging — that stays inside each `{Resource}.Application` runtime.
- A full drift-reconcile loop with server-side apply and informer resync — that is specified for
  the Kubernetes gateway; the local gateway's supervisor is the equivalent for processes.
