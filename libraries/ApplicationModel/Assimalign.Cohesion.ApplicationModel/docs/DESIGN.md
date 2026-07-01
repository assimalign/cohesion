# Assimalign.Cohesion.ApplicationModel — DESIGN

> This is the per-library design record for the **Layer 1** ApplicationModel
> contract package. The full multi-package architecture (gateways, the Kubernetes
> build-override + self-hosted registry, resource manifest packages, MSBuild
> codegen) lives in the ApplicationModel area-root `../../DESIGN.md`. Read that for
> the whole picture; read this for why *this* package looks the way it does.

## What this library is

`Assimalign.Cohesion.ApplicationModel` is the **declarative + control-plane
contract package**: a `Core`-only set of interfaces and value objects that let an
author *declare a graph of resources* (the desired state) and hand it to a
*gateway* that *realizes* it. It contains **no hosting, DI, configuration, logging,
or platform code**, and depends only on `Assimalign.Cohesion.Core`.

Two planes share one vocabulary here:

- **Declarative plane** — `IApplication`, `IApplicationModel`, `IApplicationBuilder`,
  `IApplicationResource` (+ the `IExecutableResource` / `IEndpointResource` /
  `IMountResource` capability interfaces), `IApplicationResourceDescriptor`,
  `IApplicationResourceCollection`, `IApplicationEnvironment`, and the
  `ResourceEndpoint` / `ResourceMount` value objects.
- **Control plane** (contracts only; implementations live in the `…Gateway.*`
  packages) — `IApplicationGateway`, `IApplicationResourceController`,
  `IResourceControlContext`, `IApplicationResourceStateManager`,
  `IApplicationResourcePackager`, and the `IResourceArtifact` family.

The only concrete types shipped here are the internal builder/model/descriptor/
collection/environment/application implementations behind the `Application` static
factory, plus the two `CohesionValueType`-generated identity wrappers
`ResourceName`/`ResourceId` and the new `ApplicationName`/`EnvironmentName`.

## Design intent and why-this-not-that

- **`IApplication` does not extend a host abstraction.** A host runs inside one
  process; an application is *described* then *realized* by a gateway across many
  processes/containers/pods it does not own. Conflating them forces single-process
  assumptions. `RunAsync` hands the model to the gateway and blocks until
  cancellation — it hosts nothing itself.
- **The graph type is `IApplicationModel`, not `IApplicationContext`.** The name
  already existed in the code and `IApplication.Model` returns it; reintroducing a
  second "context" type was rejected as drift.
- **`Descriptors` is authoritative; `Resources` is a projection.** Dependency edges
  live on the descriptors (the gateway topologically sorts them); the model exposes
  `Resources` as a read-only one-to-one projection for convenience, and the mutable
  working collection lives only on the builder. Surfacing a mutable `IList` on an
  "immutable desired state" was a contradiction that an early review caught.
- **Capability interfaces over one fat resource type.** A resource opts into
  `IExecutableResource` / `IEndpointResource` / `IMountResource`; gateways
  pattern-match (`resource is IExecutableResource`, AOT-safe) and ignore
  capabilities they do not understand. A local gateway needs only the first; the
  Kubernetes gateway needs all three. New gateways add new capability interfaces in
  their own package without editing this contract, and the manifest never grows a
  feature-flag matrix.
- **`UseGateway` is mandatory — no reflection.** An earlier design reflected a
  default gateway when none was set; that would have propagated a
  `RequiresUnreferencedCode` marker onto `Build()` (the one API every consumer
  calls) and fought the repo's AOT mandate. `Build()` now throws a plain
  `InvalidOperationException` when no gateway is selected. Any future zero-config
  default must be a compile-time, source-generated registration, never a runtime
  probe.
- **Readiness is a level-triggered, terminal-set wait.**
  `IApplicationResourceStateManager.WaitForStateAsync` completes on **any** state in
  a supplied terminal set (for example `{ Running, Failed }`) with a time budget, so
  a failed or crash-looping dependency can never deadlock a dependent. The lifecycle
  enum is treated as a membership set, never an ordered lattice.
- **Controllers are pure, level-triggered reconcilers.** `ReconcileAsync` computes
  desired objects and applies them, idempotently, and returns; it does not own
  steady-state observation (that is a gateway's single informer) and does not block
  on readiness (the gateway gates on the state manager).
- **Typed artifacts, no discriminator downcasts.** `IResourceArtifact` is refined by
  `IExecutableArtifact` / `IContainerImageArtifact`; consumers request the concrete
  shape by type rather than switching on a kind enum and casting.

## Lifecycle and error model

- `Application.CreateBuilder()` → fluent `AddResource(...).DependsOn(...)` +
  `UseGateway(...)` → `Build()`.
- `Build()` validates: unique resource names (enforced eagerly on `AddResource`),
  all dependencies present, no dependency cycles (DFS), and a gateway selected.
  Every failure is an `InvalidOperationException` with an actionable message; there
  are no custom exception types in this library (an area-scoped root can be added
  later if the surface grows).
- `IApplication.RunAsync` mirrors `Host<TContext>.RunAsync`: a linked
  `CancellationTokenSource` plus a `TaskCompletionSource` completed on cancellation.
  It `StartAsync`es the gateway, awaits cancellation, then `StopAsync`es within a
  bounded shutdown window (default 30s).

## AOT posture

This package is `Core`-only and AOT-clean: capability matching is `is`-based, there
is **no reflection** (the reflective gateway resolver was removed), and the only
runtime-code-adjacent call is `Assembly.GetEntryAssembly()` for a default
application name (AOT-safe, with a literal fallback). The generated value types use
`System.Text.Json` converters that are source-emitted, not reflection-based.

**Note on the family:** the sibling `…Gateway` base and `…Gateway.Docker` packages
are also AOT-gated, but `…Gateway.Kubernetes` is a **documented AOT exception**
because `KubernetesClient` pulls `YamlDotNet` and serializes via runtime reflection.
That exception is scoped to the Kubernetes gateway package and does not affect this
contract library. See the area-root `../../DESIGN.md` §13.

## Family relationships

- `…ApplicationModel.Gateway` (Layer 2a) — the guided `ApplicationGateway` base +
  `LocalGateway`; implements the control-plane contracts defined here.
- `…ApplicationModel.Gateway.{Platform}` (Layer 2b) — Kubernetes, Docker, … .
- `{Resource}.ApplicationModel` (Layer 3d) — manifest packages that reference
  **this package only**, provide `Add{Resource}(name)` extensions and an
  `IApplicationResource` (+ capability) implementation, and advertise a pre-built
  container image via reference-free MSBuild metadata.

## Non-goals

- Hosting a process (DI/Config/Logging composition stays in `{Resource}.Application`).
- Building container images (delegated to the SDK container tooling, upstream, per
  resource).
- Referencing or rebuilding any `{Resource}.Application` runtime — the orchestrator
  only ever references contract + manifest packages.
- Serialization/manifest formats, Kubernetes types, or process supervision — all of
  that lives in the gateway packages.
