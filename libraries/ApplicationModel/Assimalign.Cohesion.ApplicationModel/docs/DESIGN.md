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
  `IApplicationResource`, `ResourceManifest`, `IManifestResource`,
  `IPlannedResource`, the platform-neutral `ResourcePlan` records,
  `IApplicationResourceDescriptor`, `IApplicationResourceCollection`, and
  `IApplicationEnvironment`. The older `IExecutableResource` /
  `IEndpointResource` / `IMountResource` capability interfaces remain as a
  compatibility surface for gateways and hand-written resources while resource
  areas move to generated manifests and plans.
- **Control plane** (contracts only; implementations live in the `…Gateway.*`
  packages) — `IApplicationGateway`, `IApplicationResourceController`,
  `IResourceControlContext`, `IApplicationResourceStateManager`,
  `IApplicationResourcePackager`, and the `IResourceArtifact` family.

The guided implementation surface is `PlannedResource`; resource-area types derive
from it and override `CreatePlan(PlanContext)` only when the generic trait mapping
is insufficient. The package also ships manifest records, immutable plan records,
typed deployer options, their source-generated JSON contexts, and the generated
identity wrappers `ResourceName`/`ResourceId` and `ApplicationName`/`EnvironmentName`.
The mutable authoring builder, descriptors, and collection remain internal; `Build()`
copies descriptor edges and manifest collections into the immutable model snapshot.

The internal application-environment implementation delegates process resolution to Core's
`AppEnvironment`. That keeps the frozen Cohesion variable name and the
`COHESION_ENVIRONMENT ?? DOTNET_ENVIRONMENT ?? "Production"` precedence rule out of the
orchestration package.

## Design intent and why-this-not-that

- **`IApplication` does not extend a host abstraction.** A host runs inside one
  process; an application is *described* then *realized* by a gateway across many
  processes/containers/pods it does not own. Conflating them forces single-process
  assumptions. `RunAsync` dispatches the requested mode: Run delegates realization
  and supervision to the gateway, while Describe writes the model with no platform
  contact. It hosts nothing itself.
- **The graph type is `IApplicationModel`, not `IApplicationContext`.** The name
  already existed in the code and `IApplication.Model` returns it; reintroducing a
  second "context" type was rejected as drift.
- **`Descriptors` is authoritative; `Resources` is a projection.** Dependency edges
  live on the descriptors (the gateway topologically sorts them); the model exposes
  `Resources` as a read-only one-to-one projection for convenience, and the mutable
  working collection lives only on the builder. Surfacing a mutable `IList` on an
  "immutable desired state" was a contradiction that an early review caught.
- **Facts in manifests; realization in plans.** `ResourceManifest` mirrors the
  `cohesion/resource/v1` build artifact and contains resource facts only. At
  `Build()`, every resource produces a `cohesion/plan/v1` `ResourcePlan` from its
  manifest, deployer options, environment, and references. `GenericPlanner` is the
  inherited default: workload kind is explicit, volume mounts produce per-replica
  claims and a governing headless service, endpoints produce services, public
  endpoints produce exposures, and probes map one-for-one. Platform gateways compile
  this IR; they do not branch on resource kind or CLR type.
- **The planning path is visible at `Build()`.** `IPlannedResource.PlannerName`
  defaults to `GenericPlanner`; an area-owned planner overrides it with the full
  stable label (for example, `Database planner`). After each plan validates,
  `Build()` writes exactly one informational line to standard error in declaration
  order, preserving standard output for describe/render documents. A named area
  planner is paired with the selected gateway's stable platform identity
  (`appa-database: Database planner → kubernetes compiler`); the inherited or
  legacy fallback is always explicit (`worker: GenericPlanner`). No CLR-type
  inspection or platform-package reference is needed.
- **Typed escape hatches stop at deployer-owned facts.** `ResourceOptions` permits
  replica and storage-size overrides. `Build()` validates replicas against the
  manifest's `maxReplicas`, then runs `ResourcePlanValidator`; platform-specific
  knobs belong to the selected gateway's compiler and never enter a resource-area
  package.
- **`UseGateway` is mandatory — no reflection.** An earlier design reflected a
  default gateway when none was set; that would have propagated a
  `RequiresUnreferencedCode` marker onto `Build()` (the one API every consumer
  calls) and fought the repo's AOT mandate. `Build()` now throws a plain
  `InvalidOperationException` when no gateway is selected. Any future zero-config
  default must be a compile-time, source-generated registration, never a runtime
  probe.
- **Readiness is a level-triggered, terminal-set wait.**
  `IApplicationResourceStateManager.WaitForStateAsync` completes on **any** state in
  the initial-readiness terminal set `{ Running, Failed, Stopped }` with a time
  budget, so a failed or cleanly stopped dependency can never deadlock a dependent.
  `Stopped` before `Running` is a readiness failure. `Degraded` is observed but is
  non-gating: it does not admit dependents initially and never re-gates them
  after `Running` admitted them. Item 26 replaces the interim static set with the
  plan-derived gate described by O30. A timeout returns the last observed state;
  caller cancellation throws `OperationCanceledException` and removes the waiter.
  The lifecycle enum is treated as a membership set, never an ordered lattice.
- **Controllers are pure, level-triggered reconcilers.** `ReconcileAsync` computes
  desired objects and applies them, idempotently, and returns; it does not own
  steady-state observation (that is a gateway's single informer) and does not block
  on readiness (the gateway gates on the state manager).
- **Typed artifacts, no discriminator downcasts.** `IResourceArtifact` is refined by
  `IExecutableArtifact` / `IContainerImageArtifact`; consumers request the concrete
  shape by type rather than switching on a kind enum and casting.

## Lifecycle and error model

- `Application.CreateBuilder(ApplicationName, args)` → fluent
  `AddResource(...).DependsOn(...)` + `UseGateway(...)` → `Build()`. `UseName`
  remains available for callers that start from the parameterless overload.
- `Build()` validates: unique resource names (enforced eagerly on `AddResource`),
  at least one realized resource, all dependencies present, no dependency cycles
  (DFS), a selected gateway, an RFC 1123 application name, each typed override, and
  every computed plan. Planning deliberately happens here rather than in MSBuild or
  when the resource is added.
  Every failure is an `InvalidOperationException` with an actionable message; there
  are no custom exception types in this library (an area-scoped root can be added
  later if the surface grows).
- In Run mode, `IApplication.RunAsync` mirrors `Host<TContext>.RunAsync`: a linked
  `CancellationTokenSource` plus a `TaskCompletionSource` completed on cancellation.
  It `StartAsync`es the gateway, awaits cancellation, then `StopAsync`es supervision
  within a bounded shutdown window (default 30s). Stop leaves persistent platform
  objects running; destructive removal is the separate Teardown mode. Describe emits
  the model document and never contacts the selected gateway.

## AOT posture

This package is `Core`-only and AOT-clean: capability matching is `is`-based and
there is **no reflection-based serialization**. Manifest, plan, and describe-mode
model documents use explicit `JsonSerializerContext` contracts. Entry-assembly name
fallback is used only in Development and is slugged before validation; assembly
attributes are never read at run time. The generated value types use
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
- `{Resource}.ApplicationModel` (Layer 3d) — Core-only manifest packages that
  provide a typed `PlannedResource`, `Add{Resource}(manifest, options)`, the area's
  planner when it differs from `GenericPlanner`, and the resource-side default
  control-plane contract served by `{Resource}.Hosting`.

## Non-goals

- Hosting a process (DI/Config/Logging composition stays in `{Resource}.Application`).
- Building container images (delegated to the SDK container tooling, upstream, per
  resource).
- Referencing or rebuilding any `{Resource}.Application` runtime — the orchestrator
  only ever references contract + manifest packages.
- Platform object formats, Kubernetes types, and process supervision — those live
  in gateway/compiler packages. The platform-neutral resource manifest, realization
  plan, and describe-mode model document are contracts of this package.
