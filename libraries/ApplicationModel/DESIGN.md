# Design Plan: Cohesion ApplicationModel Abstraction (Refined v2.1)

> **Status:** This revision supersedes the original "thick composition" draft and
> the first "thin contract + process-spawning gateway" refinement. It reconciles
> the design with the abstractions now on disk — which have moved toward a
> **control-plane / reconciliation** shape (`IApplicationResourceController`,
> `IApplicationResourceStateManager`) — and folds in the architect's end-state for
> the **Kubernetes Gateway**: a build that defaults to per-resource container
> images, an orchestrator that *is its own container registry*, and Kubernetes
> orchestration driven by a Kubernetes client.
>
> **v2.1 incorporates a 4-lens adversarial review.** Three blockers were fixed in
> the design: (1) image ownership is **inverted to the resource** so the build
> needs no forbidden reference to discover what to containerize; (2) the readiness
> gate is given a **terminal-set + level-triggered** contract so it cannot deadlock
> on crash-loops; (3) the Kubernetes gateway is **scoped out of the AOT mandate** as
> a documented exception, because `KubernetesClient` is not trim-safe. Reflective
> gateway discovery is removed; steady-state ownership is given a single owner;
> image refs are digest-pinned. Sections changed by the review are marked **[R]**.

---

## 1. The One-Paragraph Mental Model

`resources/` holds **standalone services** that each know how to run themselves
(`{Resource}.Application` — an `Exe` with its own DI/Config/Logging composition).
`frameworks/` + `sdks/` are how those services are **packaged and consumed**. The
**ApplicationModel** is the *convention that binds them into one architecture*: it
lets an author **declare a graph of resources** (the *desired state*), then hands
that graph to a **Gateway** that **realizes it** on a target — local processes,
Docker, or Kubernetes. ApplicationModel is therefore split into two planes:

| Plane | Owns | Package(s) | Depends on |
|---|---|---|---|
| **Declarative plane** — *"what should exist"* | the resource graph, names, dependency edges, environment | `Assimalign.Cohesion.ApplicationModel` | **Core only** |
| **Control plane** — *"make it so, keep it so"* | packaging/gathering artifacts, deploying, reconciling, supervising, tearing down | `…ApplicationModel.Gateway[.{Platform}]` | the declarative plane + platform SDKs |

The declarative plane is a *contract package* anything can reference cheaply. The
control plane is where heavy, platform-specific machinery lives. **A resource never
knows it is orchestrated; the orchestrator never knows how a resource hosts
itself, and never references or rebuilds it.**

---

## 2. Why This Supersedes The Earlier Plans

| Concern | v0 (thick) | v1 (thin + process gateway) | **v2.1 (this plan)** |
|---|---|---|---|
| `IApplication` | extends `IHost` | `RunAsync()` only | `RunAsync()` + `Model` accessor (matches code on disk) |
| Desired-state type | n/a | `IApplicationContext` | **`IApplicationModel`** (already in code; `IApplicationContext` not reintroduced) |
| Gateway shape | n/a | `StartAsync/StopAsync` only | a **control plane**: `IApplicationGateway` drives `IApplicationResourceController`s against a level-triggered `IApplicationResourceStateManager` |
| How a resource becomes deployable | in-proc only | "exe adjacent on disk" | local → exe; container targets → **a pre-built image the resource owns**, gathered by reference-free metadata **[R]** |
| Kubernetes story | none | hand-wave | **first-class**: gather per-resource images → orchestrator-hosted registry → digest-pinned, client-driven reconcile **[R]** |
| Default gateway | n/a | reflective discovery | **explicit `UseGateway` (no reflection)** **[R]** |
| DI/Config/Logging | centralized | pushed into `{Resource}.Application` | unchanged from v1 |

Kept from v1: ApplicationModel depends on `Core` only, and `ResourceId`/`ResourceName`
remain `CohesionValueType`-generated. What changes is the gateway is a **reconciling
control plane**, and the build is a **gather**, not a fan-out publish.

---

## 3. Current State On Disk (Reconciled)

`libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/`:

| File | Current surface | Verdict |
|---|---|---|
| `IApplication.cs` | `IApplicationModel Model`, `RunAsync(ct)` | **Keep.** |
| `IApplicationModel.cs` | `IApplicationResourceCollection Resources` | **Reshape** — `Descriptors` authoritative, `Resources` a read-only projection (§4.2) **[R]**. |
| `IApplicationBuilder.cs` | `AddResource(...) : IApplicationBuilder` ×2, `Build()` | **Change:** `AddResource` returns `IApplicationResourceDescriptor`; add `UseGateway`. |
| `IApplicationResource.cs` | `ResourceId Id`, `ResourceName Name` | **Keep.** Capability interfaces layer on top (§4.5). |
| `IApplicationResourceCollection.cs` | `: IList<IApplicationResource>` | **Keep** (builder-side; impl enforces name-uniqueness). |
| `IApplicationResourceDescriptor.cs` | `Resource`, `DependsOn(descriptor)` | **Extend:** `Dependencies`, `params` overload. |
| `IApplicationGateway.cs` | `StartAsync(ct)`, `StopAsync(ct)` | **Change:** take `IApplicationModel`; add `Name`. |
| `IApplicationResourceController.cs` | empty (good summary) | **Flesh out** — a pure level-triggered reconciler (§5.1) **[R]**. |
| `IApplicationResourceStateManager.cs` | empty | **Flesh out** — level-truth store + terminal-set wait (§5.2) **[R]**. |
| `IApplicationEnvironment.cs` | empty marker | **Flesh out** — environment metadata (§4.7). |
| `Extensions/ApplicationModelExtensions.cs` | empty | **Fill** — `UseLocalGateway()`-style helpers, `GetResource<T>()`, topo helper. |

csproj references `Assimalign.Cohesion.Core` only and generates the two value types
via `CohesionValueType`. **Both invariants are preserved.** v1's `IApplicationContext`
and `IApplicationResourcePublisher` are **not** adopted: the graph type is
`IApplicationModel`; "publishing" is subsumed by the controller's reconcile +
packager's gather (§5).

---

## 4. Declarative Plane — Core Abstractions (Layer 1, `Core` only)

### 4.1 `IApplication`

```csharp
public interface IApplication
{
    /// <summary>The desired-state resource graph this application realizes.</summary>
    IApplicationModel Model { get; }

    /// <summary>Hands <see cref="Model"/> to the configured gateway, starts
    /// realization, blocks until <paramref name="cancellationToken"/> signals,
    /// then tears down gracefully.</summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}
```

### 4.2 `IApplicationModel` **[R]**

The immutable desired state. **`Descriptors` is the single source of truth**; the
mutable collection lives only on the builder. `Resources` is a read-only 1:1
projection for convenience.

```csharp
public interface IApplicationModel
{
    /// <summary>Stable application name — the Kubernetes namespace, the local log
    /// prefix root, the registry repo prefix.</summary>
    ApplicationName Name { get; }

    /// <summary>The environment the application is realized into.</summary>
    IApplicationEnvironment Environment { get; }

    /// <summary>The dependency descriptors — authoritative. Edges live here; the
    /// gateway topologically sorts these.</summary>
    IReadOnlyList<IApplicationResourceDescriptor> Descriptors { get; }

    /// <summary>Read-only projection: <c>Descriptors.Select(d =&gt; d.Resource)</c>.
    /// Invariant: <c>Resources.Count == Descriptors.Count</c>, 1:1.</summary>
    IReadOnlyList<IApplicationResource> Resources { get; }
}
```

`ApplicationName` is a String `CohesionValueType` alongside `ResourceName`/`ResourceId`.

### 4.3 `IApplicationBuilder`

```csharp
public interface IApplicationBuilder
{
    /// <summary>Adds a resource; returns its descriptor so edges chain fluently.</summary>
    IApplicationResourceDescriptor AddResource(IApplicationResource resource);

    /// <summary>Adds a resource produced from the in-progress model.</summary>
    IApplicationResourceDescriptor AddResource(Func<IApplicationModel, IApplicationResource> configure);

    /// <summary>Selects the gateway that realizes the model. Fluent. REQUIRED —
    /// <see cref="Build"/> throws if no gateway was selected (§6.4). No reflection.</summary>
    IApplicationBuilder UseGateway(IApplicationGateway gateway);

    /// <summary>Validates the graph (unique names, no cycles, all deps present,
    /// a gateway set) and returns the runnable application.</summary>
    IApplication Build();
}
```

The return-type change unlocks `builder.AddWebApp("administration").DependsOn(identityHub);`.

### 4.4 `IApplicationResourceDescriptor`

```csharp
public interface IApplicationResourceDescriptor
{
    IApplicationResource Resource { get; }
    IReadOnlyList<IApplicationResourceDescriptor> Dependencies { get; }
    IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource);
    IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources);
}
```

### 4.5 `IApplicationResource` and capability interfaces **[R]**

Base contract stays minimal:

```csharp
public interface IApplicationResource
{
    ResourceId Id => Guid.AsDeterministicGuid(Name);
    ResourceName Name { get; }
}
```

A resource carries *what a gateway needs* by **also implementing small capability
interfaces** (Layer 1, `Core`-only data shapes). Gateways pattern-match these
(`resource is IExecutableResource` — AOT-safe); a gateway ignores capabilities it
does not understand.

```csharp
/// <summary>Runs as an executable/container with an artifact identity and an
/// environment-variable contract.</summary>
public interface IExecutableResource : IApplicationResource
{
    /// <summary>Conventional artifact identity, e.g. "Assimalign.Cohesion.Web.Application".
    /// Local gateway resolves it to an exe; container gateways resolve it to the
    /// pre-built image advertised by the manifest package (§7.1).</summary>
    string Artifact { get; }

    /// <summary>Environment variables injected into the realized process/container.
    /// Named <c>EnvironmentVariables</c> (not <c>Environment</c>) to avoid colliding
    /// with <see cref="IApplicationModel.Environment"/>.</summary>
    IReadOnlyDictionary<string, string> EnvironmentVariables { get; }
}

/// <summary>Exposes one or more *declared* (desired) network endpoints. Observed/
/// allocated endpoints (OS-assigned ports, NodePort/Ingress hosts) are NOT here —
/// they flow through the state manager's observed view (§5.2, §14.4).</summary>
public interface IEndpointResource : IApplicationResource
{
    IReadOnlyList<ResourceEndpoint> Endpoints { get; }
}

/// <summary>Mounts configuration/secret/volume data.</summary>
public interface IMountResource : IApplicationResource
{
    IReadOnlyList<ResourceMount> Mounts { get; }
}
```

`ResourceEndpoint` (name, scheme, port, public/internal) and `ResourceMount` (name,
path, kind) are **hand-authored `readonly record struct`s** in `Core`-only Layer 1.
*(Why hand-authored, not `CohesionValueType`: that generator emits single-backing-type
identity wrappers; these are multi-field aggregates. Single-runtime-type identity
wrappers — `ResourceName`, `ApplicationName`, `EnvironmentName` — use `CohesionValueType`.)*

> **Why capabilities, not one fat type:** `LocalGateway` needs only
> `IExecutableResource`; the Kubernetes gateway needs all three to emit
> `Deployment` + `Service` + `ConfigMap`. New gateways add new capability
> interfaces in their own package without touching Layer 1.

### 4.6 `IApplicationResourceCollection`

```csharp
public interface IApplicationResourceCollection : IList<IApplicationResource>;
```

This mutable collection is a **builder-side** type (the model exposes a read-only
projection, §4.2). The internal impl throws on duplicate `ResourceName`.

### 4.7 `IApplicationEnvironment`

```csharp
public interface IApplicationEnvironment
{
    EnvironmentName Name { get; }            // "Development", "Production"
    bool IsDevelopment { get; }              // gateways may pick daemon-load / relaxed readiness
}
```

`EnvironmentName` is a String `CohesionValueType`.

### 4.8 Static factory `Application`

```csharp
public static class Application
{
    public static IApplicationBuilder CreateBuilder() => new ApplicationBuilder();
    public static IApplicationBuilder CreateBuilder(string[] args) => new ApplicationBuilder(args);
}
```

### 4.9 Internal implementations (`src/Internal/`) **[R]**

- `ApplicationBuilder` — the only `IApplicationBuilder`. Holds the resource
  collection, descriptor list, and chosen gateway. `Build()` validates (unique
  names, acyclic, deps present, **gateway set — else `InvalidOperationException`
  with an actionable message; no reflection**) and returns `CohesionApplication`.
- `ApplicationModel` — desired-state record; `Descriptors` authoritative, `Resources`
  a computed projection.
- `ApplicationResourceCollection` — name-unique `List<IApplicationResource>`.
- `ApplicationResourceDescriptor` — resource + `DependsOn` edges.
- `ApplicationEnvironment` — reads `DOTNET_ENVIRONMENT`/`COHESION_ENVIRONMENT`.
- `CohesionApplication` — the only `IApplication`. `RunAsync` mirrors
  `Host<TContext>.RunAsync` (linked `CancellationTokenSource` + a
  `TaskCompletionSource` completed on cancellation):

  ```csharp
  public async Task RunAsync(CancellationToken cancellationToken = default)
  {
      using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
      cts.Token.Register(static s => ((TaskCompletionSource)s!).TrySetResult(), stopped);

      await _gateway.StartAsync(_model, cts.Token).ConfigureAwait(false);
      await stopped.Task.ConfigureAwait(false);

      using var stopCts = new CancellationTokenSource(_shutdownTimeout);
      await _gateway.StopAsync(stopCts.Token).ConfigureAwait(false);
  }
  ```

> **No `GatewayResolver`.** v2 proposed reflecting `LocalGateway` when `UseGateway`
> was unset. That is removed: `RequiresUnreferencedCode` would poison `Build()` —
> the one API every consumer calls — for AOT, and the `DynamicDependency` would
> need a type the assembly deliberately avoids. `UseGateway`/`UseLocalGateway()` is
> mandatory. *Optional* zero-config can later be a **compile-time** edge: the
> Gateway package source-generates a module-initializer that registers an ambient
> default (mirroring `CohesionValueType` codegen) — never runtime reflection.

---

## 5. Control Plane — Gateway Abstractions

Still **Layer 1** (`Core`-only interfaces; implementations live in Layer 2), so the
model and any gateway share one vocabulary.

### 5.1 `IApplicationResourceController` **[R]**

A **pure, level-triggered reconciler** for one resource: compute desired, apply,
return. It does **not** own steady-state ("keep it there" is the gateway's single
informer, §5.3) and must be safe to re-run.

```csharp
public interface IApplicationResourceController
{
    /// <summary>True if this controller realizes the resource (typically by
    /// matching a capability interface or resource type).</summary>
    bool CanControl(IApplicationResource resource);

    /// <summary>Computes the desired platform objects for the resource and applies
    /// them (server-side apply on K8s). Idempotent: called on first deploy and on
    /// every reconcile tick. Returns; it does not block on readiness.</summary>
    Task ReconcileAsync(IResourceControlContext context, CancellationToken cancellationToken = default);

    /// <summary>Removes the resource's realized objects (reverse of reconcile).</summary>
    Task DeleteAsync(IResourceControlContext context, CancellationToken cancellationToken = default);
}

public interface IResourceControlContext
{
    IApplicationResource Resource { get; }
    IApplicationModel Model { get; }
    IApplicationResourceStateManager State { get; }
    IReadOnlyList<IApplicationResource> Dependencies { get; }
}
```

**Controller resolution (§5.3 routing).** Controllers are registered in order on the
gateway. For a resource, the gateway picks the **first** whose `CanControl` returns
true (most-specific registered first). If **none** match → throw with an actionable
message naming the resource and its capabilities. `>1` matching is allowed and
resolved by first-match; controllers are expected to be mutually exclusive by
capability conjunction (e.g. the K8s controller matches `IExecutableResource &&
IEndpointResource`; the local one matches `IExecutableResource` only).

### 5.2 `IApplicationResourceStateManager` **[R]**

The **level-triggered source of truth** for observed state — *not* an event log.
A single per-gateway informer (§5.3) is its only writer. The readiness wait
completes on **any terminal in a set** (so a `Failed` dependency cannot deadlock a
dependent) and is race-free.

```csharp
public interface IApplicationResourceStateManager
{
    /// <summary>Current observed state.</summary>
    ResourceLifecycle GetState(ResourceId id);

    /// <summary>Observed status + optional allocated endpoints (§14.4). Idempotent
    /// level write; safe to call repeatedly with the same value. Raises StateChanged.</summary>
    void SetState(ResourceId id, ResourceLifecycle state, string? detail = null,
                  IReadOnlyList<ResourceEndpoint>? observedEndpoints = null);

    /// <summary>Observed (allocated) endpoints for a resource once known.</summary>
    IReadOnlyList<ResourceEndpoint> GetObservedEndpoints(ResourceId id);

    /// <summary>Completes when the resource reaches ANY state in
    /// <paramref name="terminals"/> (e.g. {Running, Failed}) or the budget/token
    /// elapses; returns the reached state. Concurrency contract: a waiter is
    /// registered under the same lock that guards the current-state read, so a
    /// SetState racing the wait cannot be lost.</summary>
    Task<ResourceLifecycle> WaitForStateAsync(
        ResourceId id, IReadOnlySet<ResourceLifecycle> terminals,
        TimeSpan budget, CancellationToken cancellationToken = default);

    event EventHandler<ResourceStateChangedEventArgs> StateChanged;
}

public enum ResourceLifecycle
{
    Unknown = 0, Pending, Building, Provisioning, Starting,
    Running, Degraded, Stopping, Stopped, Failed, Blocked, Skipped
}
```

`Blocked`/`Skipped` mark dependents of a failed prerequisite (§5.3). The enum is
**not** treated as an ordered lattice — waits are membership tests, never "reached
or passed".

### 5.3 `IApplicationGateway` (refined) **[R]**

```csharp
public interface IApplicationGateway
{
    ResourceName Name { get; }   // "local", "docker", "kubernetes"
    Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
```

A base **`abstract class ApplicationGateway : IApplicationGateway`** implements the
generic algorithm once. Per the repo *interface-first-with-guided-base* rule, the
interface members are implemented **explicitly** and forward to strongly-typed
`protected abstract` hooks the platform overrides:

```csharp
public abstract class ApplicationGateway : IApplicationGateway
{
    public abstract ResourceName Name { get; }

    async Task IApplicationGateway.StartAsync(IApplicationModel model, CancellationToken ct)
        => await StartCoreAsync(model, ct).ConfigureAwait(false);

    // hooks the platform implements:
    protected abstract Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken ct);
    protected abstract IReadOnlyList<IApplicationResourceController> Controllers { get; }
    protected abstract IApplicationResourceStateManager State { get; }
    protected abstract Task StartObserverAsync(IApplicationModel model, CancellationToken ct); // the single informer
    // ...StartCoreAsync runs the algorithm below using these.
}
```

The generic algorithm (`StartCoreAsync`):

1. Validate + **topologically sort** `model.Descriptors` (cycles fail fast).
2. **Gather** each resource's artifact (`GatherAsync`) — local resolves an exe;
   container gateways resolve the pre-built image (§7.1). No building here.
3. Start the **single per-gateway observer/informer** — the sole writer of observed
   status into `State` (§5.2), and the sole owner of the platform watch.
4. For each resource in topo order: route to its controller (§5.1), `ReconcileAsync`
   (apply only — non-blocking), then
   `await State.WaitForStateAsync(id, {Running, Failed}, budget, ct)`:
   - `Running` → start dependents.
   - `Failed`/timeout → mark the dependent subtree `Blocked`/`Skipped`, and per the
     configured policy either **fail fast** (throw an aggregated error naming the
     resource + detail) or continue with the reachable subgraph.
5. **Reconcile loop** until `StopAsync`: a periodic re-list + the informer keep
   `State` level-true; on drift (observed ≠ desired by `observedGeneration`),
   re-`ReconcileAsync` with bounded backoff. Controllers use **server-side apply
   with a fixed field manager** so the loop ignores its own writes; `resourceVersion`
   conflicts are retried.
6. `StopAsync`: `DeleteAsync` in reverse topo order with per-resource grace.

### 5.4 `IApplicationResourcePackager` and typed artifacts **[R]**

The **gather** seam — produces/locates the deployable artifact a controller consumes.

```csharp
public interface IApplicationResourcePackager
{
    Task<IResourceArtifact> GatherAsync(IApplicationResource resource, CancellationToken cancellationToken = default);
}

public interface IResourceArtifact { ResourceId Resource { get; } }

/// <summary>A resolved executable on disk (local gateway).</summary>
public interface IExecutableArtifact : IResourceArtifact { string ExecutablePath { get; } }

/// <summary>A pre-built container image, pinned by digest (container gateways).</summary>
public interface IContainerImageArtifact : IResourceArtifact
{
    string Repository { get; }       // e.g. "web-administration"
    string Digest { get; }           // "sha256:..."  — content-addressed, build-time-immutable
    string? Tag { get; }             // human-readable only; never the pull ref
}
```

Consumers request a typed artifact (`context.GetArtifact<IContainerImageArtifact>()`)
which throws a specific exception on kind mismatch — no enum discriminator, no
unchecked downcast (interface-first per the repo coding rules). Local's packager returns an
`IExecutableArtifact`; container gateways return an `IContainerImageArtifact`
resolved from `application.images.json` (§7.1) — **gather validates/loads, it does
not build**.

---

## 6. Layer 2a — `ApplicationModel.Gateway` (base + LocalGateway)

New project `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/`,
references `Assimalign.Cohesion.ApplicationModel`. **AOT-clean and AOT-gated.**

Contents:
- `ApplicationGateway.cs` — the guided base (§5.3).
- `LocalGateway.cs` — default for local dev; packager resolves `{Artifact}.exe`
  adjacent to the orchestrator (dev fallback: `dotnet run` against a referenced
  project). Its observer is a process-status poller.
- `LocalProcessController.cs` — reconciles a resource to a running child process;
  readiness via stdout marker / port-listen probe; writes `ResourceLifecycle` +
  **observed endpoints** (the actually-bound port) into the state manager; pipes
  child stdout/stderr with a `[ResourceName]` prefix.
- `LocalGatewayProcessSupervisor.cs` — process lifetimes, restart policy, SIGTERM
  (Ctrl-C on Windows) → grace → SIGKILL.
- `Internal/InMemoryResourceStateManager.cs` — the default level-truth store
  (the §5.2 lock/wait contract), reused by all gateways.
- `Extensions/LocalGatewayExtensions.cs` — `UseLocalGateway()` on `IApplicationBuilder`.

### 6.4 Gateway selection (no reflection) **[R]**

`UseGateway(...)`/`UseLocalGateway()` is **mandatory**. `Build()` throws a plain
`InvalidOperationException("No IApplicationGateway selected. Call UseGateway(...) or
UseLocalGateway().")` when unset. If a zero-config default is later wanted, it is a
**compile-time** source-generated module-initializer in the Gateway package, never a
runtime probe (rationale in §4.9).

---

## 7. Layer 2b — The Kubernetes Gateway (the headline)

Project `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes/`.
It references `KubernetesClient` via `CohesionPackageReference Include="KubernetesClient"`
(no inline version; centralized — already pinned v17.0.4) and the transitive
`YamlDotNet` (kubeconfig parsing) is acknowledged and added to the centralized list.
**This package is scoped OUT of the AOT mandate** (§13) — `KubernetesClient` 17.0.4
is not trim-safe.

The architect's end-state, made concrete, has **three movements**.

### 7.1 Movement 1 — Build: every resource owns its own image; the orchestrator *gathers* **[R]**

The v2 "orchestrator fan-out publishes each `{Resource}.Application`" is replaced —
it required a forbidden reference and broke for NuGet consumers. Inverted ownership:

- **Each `{Resource}.Application` owns a `PublishContainer` step** in *its own*
  build/CI (it already consumes a Cohesion SDK). It emits an **OCI tarball** via
  `ContainerArchiveOutputPath` (the only supported sink — **no Docker daemon
  assumption**) plus a **per-resource image-index fragment**
  (`ResourceName → repo@sha256:digest`).
- **`{Resource}.ApplicationModel`** (the manifest package) *ships* that tarball +
  fragment and advertises a **build-only, non-flowing** MSBuild item
  `CohesionResourceImageArtifact` (artifact name + relative path to the bundled
  tarball/index). This is **metadata, not a `ProjectReference`** — the §3/§13
  no-reference promise holds at the assembly level, and the image travels *inside*
  the manifest NuGet package so external consumers get it without any source tree.
- **The orchestrator's build-override** `CohesionBuildResourceContainers` is reduced
  to a **gather/merge**: read the `CohesionResourceImageArtifact` metadata
  transitively contributed by the **declared** manifest packages (scoped to
  `model`'s resources / declared items — *not* a `resources/` glob), copy their
  tarballs into `<out>/images/`, concatenate fragments into `application.images.json`.
  No string→csproj resolver, no N-child publish fan-out, no requirement that
  `{Resource}.Application` sources exist on the consumer's disk.

This still satisfies *"override the build process … default to container builds for
each resource/app"* — the default shippable path is **pre-built per-resource images
consumed by reference-free metadata**. *On-the-fly rebuild-from-source* (the monorepo
dev loop) is a **separate, opt-in** path: an explicit N-publish fan-out with concrete
RID, `Inputs/Outputs` incrementality, and failure aggregation — never the default.

### 7.2 Movement 2 — The orchestrator *is* its own container registry **[R]**

On `StartAsync`, the `KubernetesGateway`:

1. **Unpacks** each bundled `images/*.tar` (docker-save/OCI archive) into a
   blobs+manifests content store — *a real adapter, budgeted, not a free
   "store-and-serve"*.
2. Starts an **embedded OCI registry** speaking the **Docker Registry HTTP API v2**
   (`/v2/`, blob HEAD/GET/PUT, manifest GET/PUT) on the in-repo HTTP stack —
   `Assimalign.Cohesion.Http.Connections.HttpConnectionListener` over
   `Assimalign.Cohesion.Connections.Tcp.TcpConnectionListener`, routed with
   `Assimalign.Cohesion.Web.Routing.IRouter` — **no ASP.NET Core**, no
   `Microsoft.Extensions.*`.
3. Binds the registry on a **node-reachable** address per topology (§7.5).

This is *"packages itself up as a runnable application that acts as its own
container registry."*

### 7.3 Movement 3 — Reconcile via the client, **by digest** **[R]**

Controllers drive the cluster through `KubernetesClient` — typed `k8s.Models`:

- `KubernetesNamespaceController` — ensures the `model.Name` namespace and the
  `imagePullSecret` (auth) **and** registry *transport trust* (§7.5 — distinct from
  auth).
- `KubernetesResourceController` (matches `IExecutableResource && IEndpointResource`)
  emits and **server-side-applies**: `V1ConfigMap` (from `EnvironmentVariables`/
  `Mounts`), `V1Deployment` whose container image is
  **`{registry}/{repo}@sha256:{digest}`** (digest from `application.images.json` —
  never a tag; tags drift and kubelet caches by tag), and `V1Service` (+ optional
  `Ingress`) from declared `Endpoints`.
- The **single informer** (list+watch, `ResourceLifecycle` derived from
  `observedGeneration == generation && updatedReplicas == readyReplicas ==
  spec.replicas`) feeds `State`; it **auto-relists on `410 Gone`/disconnect** and a
  periodic re-list keeps `State` level-true. Apply happens in topo order; dependents
  gate on the §5.2 terminal-set wait.
- `StopAsync` deletes the namespace (reverse order) and stops the registry.

This is *"orchestrates the Kubernetes deployments via a Kubernetes client."*

### 7.4 Image construction & tooling — lean on SDK/daemon **[R]**

Per the agreed scope, the gateway does **not** hand-roll OCI layer/manifest
*construction*: each resource's image is built **once, upstream** by
`Microsoft.NET.Build.Containers` (`PublishContainer`) — an **SDK pack bundled with
the `global.json`-pinned .NET SDK**, so no per-project version pin and *not* a
forbidden `Microsoft.Extensions.*` runtime package. The only supported sink is
`ContainerArchiveOutputPath` (loose OCI tarball; daemon-optional). The runtime side
only **unpacks + serves** (§7.2). An **AOT/trim spike on KubernetesClient 17.0.4 is a
gating prerequisite** for this package, with a hand-rolled typed-REST client over
`Assimalign.Cohesion.Http` as the documented fallback if the spike fails.

### 7.5 Node reachability & registry trust (first-class prerequisites) **[R]**

A self-hosted registry only works if **nodes can pull from it**, over a trusted
transport, by digest. Topology is selected by `IApplicationEnvironment`:

| Topology | Node→registry path | Trust precondition | When |
|---|---|---|---|
| **In-cluster** | Orchestrator runs as a Pod; registry via a **NodePort/host-reachable** address (a ClusterIP DNS name is usually *not* resolvable on the kubelet/containerd pull path) | TLS with a distributed cert, **or** per-node insecure-registry config (`hosts.toml`/`registries.yaml`) — a **cluster-admin day-0 mutation**; bounds in-cluster to non-managed nodes (not EKS/GKE/AKS self-provisionable) | Real clusters |
| **Local-host registry** | Orchestrator on the host; registry on a host address; local clusters (kind/k3d/minikube/Docker Desktop) configured with a containerd mirror to `host.docker.internal:<port>` | local insecure-registry mirror | Local dev |
| **Daemon-load fast path** | Skip pull: `kind load` / `docker load` / `nerdctl load` the tarballs onto **all** nodes (or pin pods to loaded nodes via nodeSelector); image keyed by **digest** so a content change forces an uncached ref; `imagePullPolicy: IfNotPresent` | none | Local dev, fastest loop |

`imagePullSecret` solves **auth**, not transport-trust or DNS-from-node — both are
called out as explicit preconditions, not assumptions. For `IsDevelopment` the
gateway prefers daemon-load when a daemon is reachable, falling back to the
local-host registry.

### 7.6 Docker gateway (sibling, smaller)

`Gateway.Docker` reuses the same packager + controllers but `docker run`s the
per-resource images against the local daemon — useful for integration tests and a
stepping-stone sharing ~80% with the Kubernetes gateway (same images, no registry/
k8s movements).

---

## 8. MSBuild Integration

### 8.1 Resource-manifest codegen (mirrors `CohesionValueType`) **[R]**

A new item type `CohesionResourceManifest` declared by `{Resource}.ApplicationModel`
projects, with a **`Capabilities`** metadatum so the generator knows which capability
interfaces the partial implements:

```xml
<CohesionResourceManifest Include="ResourceManifest/WebResource.cs"
                          ResourceType="WebApp"
                          Artifact="Assimalign.Cohesion.Web.Application"
                          Capabilities="Executable;Endpoint;Mount"
                          DefaultPort="8080"
                          ObjectNamespace="Assimalign.Cohesion.Web.ApplicationModel"
                          ObjectAccessModifier="public" />
```

A new task `CodeGenerationCreateResourceManifestTask` (under
`build/Tasks/Tasks/CodeGeneration/`, inheriting `CodeGenerationTask`, mirroring
`CodeGenerationCreateValueTypeTask`) emits a `partial class` implementing
`IApplicationResource` **plus the listed capability interfaces** (hand-authored
partials add behavior), and an `Add{ResourceType}(this IApplicationBuilder, string
name)` extension. Wired in `build/Targets/Build.CodeGeneration.{props,targets}` next
to the value-type task, `BeforeTargets="CoreCompile"`, with a clean target.

### 8.2 The container gather target **[R]**

A new `build/Targets/Build.ApplicationModel.Containers.targets` (imported by the
Kubernetes/Docker SDK, or gated on `<CohesionGateway>`), hung off the reserved
`Assimalign.Cohesion.Sdk.ApplicationModel.Build.targets` seam:

- `CohesionBuildResourceContainers` — `AfterTargets="Build"`: reads the
  `CohesionResourceImageArtifact` metadata contributed by the **declared** manifest
  packages (scoped to declared resources, never a `resources/` glob), copies their
  pre-built tarballs into `<out>/images/`, writes the merged `application.images.json`.
  Honors `CleanDependsOn`.

It is a **gather**, not a publish — image production happens upstream per resource
(§7.1).

---

## 9. Resource Patterns (concrete)

### 9.0 Verified inventory (18 areas, 167 projects)

Pinned from a full sweep of `resources/`:

- **`IApplicationResource` is implemented nowhere yet** — every resource manifest is
  greenfield. There is no existing pattern to preserve, only to establish.
- **Real `Host<TContext>` runtimes (→ `{Resource}.Application`):**
  - **Scheduler** — `ScheduleApplication : Host<ScheduleContext>, IScheduler`,
    `ScheduleApplicationBuilder : ISchedulerBuilder`, `ScheduleContext`,
    `ScheduleOptions`. Clean rename target.
  - **Web** — `WebApplication : Host<WebApplicationContext>`, full builder/context/
    options; references `Http.Connections`, `Connections[.Tcp/.Quic]`. Clean rename
    target. (Web also ships `Web.Server` + 12 feature projects.)
- **Stub `.Hosting` projects (defer):** Database, ApiManager, IoTHub — empty shells.
  Not rename candidates until they have real runtimes. (`Database` base already
  declares an `IDatabaseApplication` — a resource-local "application" notion; keep it
  distinct from the orchestration `IApplication`.)
- **Legacy builder-extension stubs (different axis, leave alone):**
  `LogSpace.AddLogSpace(this IHostBuilder)`, `LoadBalancer.AddLoadBalancer(this
  IHostBuilder)` extend `IHostBuilder` (in-process), not `IApplicationBuilder`.
- **The Web competing implementation (delete + rebuild):**
  `resources/Web/Assimalign.Cohesion.Web.ApplicationModel/src/` holds a second
  `WebApplication : IWebApplication, IWebApplicationPipelineBuilder` (not `Host<T>`,
  unimplemented `Dispose()`/`StopAsync()`) that references **nine runtime libraries**
  plus `ApplicationModel` — the "tried to be both manifest and runtime" violation. A
  manifest project must reference **`ApplicationModel` only**. Delete its `src/`,
  rebuild per §9.4.

### 9.1 Layer 3a `{Resource}` (base) — unchanged
Pure domain (`IScheduler`, `ISchedule`; `IRouter`, `IHttpContext`).

### 9.2 Layer 3b `{Resource}.{Feature}` — unchanged
`Scheduler.Cron`, `Scheduler.Timer`, `Web.Routing`, … Integrated only inside
`{Resource}.Application`; invisible to the manifest.

### 9.3 Layer 3c `{Resource}.Application` (standalone Exe)
Rename of `{Resource}.Hosting`. Owns DI/Config/Logging via `Host<TContext>`; ships a
`Program.cs`. **Does NOT reference `Assimalign.Cohesion.ApplicationModel`.** **Owns
its own `PublishContainer`** producing the OCI tarball + image-index fragment (§7.1).

### 9.4 Layer 3d `{Resource}.ApplicationModel` (manifest-only)
References `Assimalign.Cohesion.ApplicationModel` **only**. Provides the
`Add{Resource}(name)` extension + the `IApplicationResource`/capability impl
(hand-written now, codegen later). **Does NOT reference `{Resource}.Application`** —
it ships the pre-built image and advertises it via `CohesionResourceImageArtifact`
metadata; the gateway resolves it by digest at gather time (§7.1).

---

## 10. Consumer Experience

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Application.CreateBuilder(args);

var dns         = builder.AddDns("dns");
var identityHub = builder.AddIdentityHub("identity-hub").DependsOn(dns);
var admin       = builder.AddWebApp("administration").DependsOn(identityHub);
var users       = builder.AddWebApp("users").DependsOn(identityHub);
var employees   = builder.AddWebApp("employees").DependsOn(identityHub);

builder.UseGateway(new KubernetesGateway());   // or .UseLocalGateway(); REQUIRED

IApplication app = builder.Build();
await app.RunAsync();
```

- `dotnet run` with the **local** gateway → five supervised child processes in
  dependency order, dependents gated on each predecessor binding.
- Build the host with the **Kubernetes SDK** → the gather target collects the five
  pre-built images into `application.images.json`; `RunAsync` starts the embedded
  registry and **digest-pins** five Deployments+Services applied to the cluster in
  topo order, gated on watch-fed readiness, namespace = the app name.

Orchestrator references: `ApplicationModel` + one
`ApplicationModel.Gateway[.Kubernetes]` + the `{Resource}.ApplicationModel` packages.
**Never** the `{Resource}.Application` runtimes.

---

## 11. Migration Plan (phased)

> **Implementation status (2026-06-30):** Phases **1 and 2 are done** (built + tested,
> 24 unit tests, on `feature/L01.01.02.01-application-model-orchestration`). Phases 3–6
> remain. Tracked as GitHub Project #13 feature #731 with tasks #732 (Phase 1) / #733 (Phase 2).

1. **Layer-1 refactor (this pass):** apply §4–§5 contracts (incl. the review
   reshapes — `Descriptors` authoritative, terminal-set `WaitForStateAsync`, no
   `GatewayResolver`, `EnvironmentVariables`, typed artifacts); add the `Application`
   factory + internal impls; add `ApplicationName`/`EnvironmentName` value types;
   unit tests. Build green; AOT-gated.
2. **Gateway base + LocalGateway (Layer 2a):** guided `ApplicationGateway`,
   `LocalGateway`, process controller/supervisor, level-truth state manager,
   `UseLocalGateway()`.
3. **Scheduler proof:** rename `Scheduler.Hosting` → `Scheduler.Application` (git mv;
   add `Program.cs` + `PublishContainer` wiring); add `Scheduler.ApplicationModel`
   (Layer 3d); integration test `AddScheduler(...).Build().RunAsync()` via LocalGateway.
4. **Web:** delete the competing `Web.ApplicationModel/src`, rebuild as Layer 3d;
   rename `Web.Hosting` → `Web.Application`; add `WebResource` + `AddWebApp`.
5. **Resource-manifest codegen:** `CodeGenerationCreateResourceManifestTask` +
   `CohesionResourceManifest` (with `Capabilities`); convert hand-written manifests.
6. **Kubernetes gateway (Layer 2b):** AOT spike on KubernetesClient first; embedded
   registry on the in-repo HTTP stack; informer-fed state; digest-pinned controllers;
   the gather target + `application.images.json`; reachability topologies. Docker
   gateway as the smaller sibling first if useful.

---

## 12. Files To Create / Modify / Delete (Layer 1, this pass)

**Modify**
- `Abstractions/IApplicationBuilder.cs` — `AddResource` → descriptor; add `UseGateway`; fix `Func<IApplicationModel,…>`.
- `Abstractions/IApplicationResourceDescriptor.cs` — add `Dependencies` + `params` overload.
- `Abstractions/IApplicationGateway.cs` — take `IApplicationModel`; add `Name`.
- `Abstractions/IApplicationModel.cs` — `Name`, `Environment`, `Descriptors` (authoritative), `Resources` (projection).
- `Abstractions/IApplicationResourceController.cs` — flesh out (§5.1).
- `Abstractions/IApplicationResourceStateManager.cs` — flesh out (§5.2, terminal-set wait).
- `Abstractions/IApplicationEnvironment.cs` — flesh out (§4.7).
- `Extensions/ApplicationModelExtensions.cs` — `GetResource<T>()`, topo helper.
- `…ApplicationModel.csproj` — add `ApplicationName`/`EnvironmentName` value types.

**Create**
- `Abstractions/IExecutableResource.cs`, `IEndpointResource.cs`, `IMountResource.cs`, `IApplicationResourcePackager.cs`, `IResourceArtifact.cs` (+ `IExecutableArtifact`, `IContainerImageArtifact`), `IResourceControlContext.cs`.
- `ValueObjects/ResourceEndpoint.cs`, `ResourceMount.cs`; `Enums/ResourceLifecycle.cs`; `Events/ResourceStateChangedEventArgs.cs`.
- `Application.cs` (factory).
- `Internal/ApplicationBuilder.cs`, `ApplicationModel.cs`, `ApplicationResourceCollection.cs`, `ApplicationResourceDescriptor.cs`, `ApplicationEnvironment.cs`, `CohesionApplication.cs`.
- `ValueTypes/ApplicationName.cs`, `EnvironmentName.cs` (codegen seeds).
- `tests/ApplicationBuilderTests.cs`, `ApplicationResourceDescriptorTests.cs`, `ApplicationStateManagerTests.cs`.

**Delete (later phases)**
- competing `resources/Web/Assimalign.Cohesion.Web.ApplicationModel/src/` tree.

---

## 13. Design Decisions & Rationale **[R]**

- **Two planes, one vocabulary.** Controller/state-manager/gateway interfaces are
  `Core`-only Layer-1 contracts; the weight (KubernetesClient, registry, process
  supervision) is isolated in Layer 2.
- **Capability interfaces over a fat resource.** Minimal base; new gateways extend
  what a resource describes without editing the contract; no feature-flag matrix.
- **`IApplication` ≠ `IHost`.** A host runs inside one process; an application is
  *described* then *realized* by a gateway across many processes/pods.
- **`{Resource}.Application` never references ApplicationModel, and the orchestrator
  never references the runtimes.** The image is built once by the resource and
  shipped *inside* the manifest package as `CohesionResourceImageArtifact` metadata —
  reference-free, NuGet-consumer-safe, no build-time fan-out.
- **Digest-pinned, self-hosted registry; SDK-built images.** Image *building* is
  delegated to the SDK container tooling (build-time, AOT-irrelevant); the runtime
  only *unpacks and serves* via the in-repo, ASP.NET-free HTTP stack; pulls are by
  digest so tags can never serve stale layers.
- **Zero reflection.** `UseGateway` is mandatory; `Build()` throws a plain exception
  when unset. Any zero-config default is a compile-time source-generated registration,
  honoring the repo rule "no runtime type inspection without source generators".
- **AOT scoping is honest.** Layer-1 contracts, `LocalGateway`, and `Gateway.Docker`
  are `IsAotCompatible=true` and AOT-gated in CI. **`Gateway.Kubernetes` is a
  documented exception** (`IsAotCompatible` omitted/false): `KubernetesClient`
  17.0.4 pulls `YamlDotNet` and serializes via runtime reflection; upstream declined
  AOT. It is excluded from the §15 PublishAot smoke, gated instead on the §7.4 spike.
- **`KubernetesClient`/`YamlDotNet` via `CohesionPackageReference`**, centralized, no
  inline versions.

---

## 14. Open Questions (carried; do not block the Layer-1 refactor) **[R]**

1. **Registry host late-binding** — `application.images.json` carries
   `repo@sha256:digest` (build-time immutable); only the **registry host** is
   late-bound at deploy time so one bundle serves in-cluster and local-host. *(Resolved
   direction; mechanics in §7.5.)*
2. **Config/secret provenance** — does `IMountResource` carry values, or references
   resolved by a `SecretStore`/`ConfigurationStore` resource at reconcile time?
3. **Controller discovery** — explicit ordered registration on the gateway (AOT-friendly,
   first-match-wins per §5.1). No assembly scan.
4. **Cross-resource service discovery** — inject a dependency's endpoint into a
   dependent via the **observed** view (`State.GetObservedEndpoints`), gated on the
   dependency reaching an *endpoint-known* sub-state — **not** its declared data.
   Local: launch the dependent only after the dependency binds its (possibly
   OS-assigned) port. K8s: inject the **stable Service DNS** (resolvable pre-`Running`);
   explicitly **forbid** injecting NodePort/Ingress allocations that don't exist until
   apply.

---

## 15. Verification **[R]**

- **Layer-1 unit tests:** `AddResource` returns a descriptor; `Build` fails on
  duplicate name / cycle / **missing gateway** (actionable message, no reflection);
  `DependsOn` (single + params); `Dependencies` read-only; `Descriptors`/`Resources`
  1:1 invariant; `CohesionApplication.RunAsync` starts then stops the gateway on
  cancellation (fake gateway records calls); **`WaitForStateAsync` returns on the
  first terminal in the set, on timeout, and is race-free** (SetState racing a wait
  is never lost).
- **Gateway tests:** topo order on start, reverse on stop; readiness gating returns
  `Failed` and marks the subtree `Blocked` without deadlock; local resolver finds an
  adjacent exe; observed-endpoint injection ordering for dynamic ports.
- **Kubernetes gateway tests:** registry serves a known blob/manifest by digest;
  controllers emit expected `k8s.Models` for a capability set with a `@sha256` image
  ref; informer relists on `410` and keeps state level-true (fake API).
- **AOT publish smoke:** `dotnet publish -p:PublishAot=true` over ApplicationModel +
  `Gateway` (base/Local) + a sample orchestrator; CI gates trim warnings. **No
  `DynamicDependency` anywhere** (reflection removed). `Gateway.Kubernetes` is
  **excluded** and gated on the §7.4 KubernetesClient AOT spike instead.
```
