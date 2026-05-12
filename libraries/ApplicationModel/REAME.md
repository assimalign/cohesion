# Design Plan: Cohesion ApplicationModel Abstraction (Refined)

## 1. Context

### Why This Plan Supersedes The Earlier Draft

An earlier draft of this file modeled `ApplicationModel` as a thick composition library that owned DI, Configuration, Logging, Hosting, and FileSystem wiring. After review the architect rejected that approach. **ApplicationModel becomes a thin orchestration contract** — the runtime composition responsibilities move down into each `{Resource}.Application` package where they actually belong, and a new **Gateway** pattern lifts deployment / process orchestration to a first-class peer concern.

### Design Shifts (Earlier → Refined)

| Concern | Earlier Plan (REJECTED) | Refined Plan (THIS plan) |
|---|---|---|
| `IApplication` | Extends `IHost`, runs in-process | Pure orchestrator with `RunAsync()` only — does NOT extend `IHost` |
| `IApplicationBuilder` | Exposes `Services`, `Configuration`, `Logging` | Exposes `AddResource()`, `Build()`, `UseGateway()` only |
| `ApplicationModel` package deps | Core, DI, Config, Logging, Hosting, FileSystem | `Core` ONLY |
| Composition (DI/Config/Logging) | Centralized in ApplicationModel base | Distributed into each `{Resource}.Application` |
| Deployment / publishing | Manifests as side-data on top of in-process runtime | First-class via Gateway pattern |
| Local development model | In-process via `.AsService()` glue | LocalGateway orchestrates `{Resource}.Application` instances as separate processes |
| Resource-level features | Optional shapes on `{Resource}.Hosting` | Resource-internal only — invisible to ApplicationModel manifest |

The architectural intent: ApplicationModel describes a *graph of resources* and asks a *gateway* to run them. It does not itself know how to host anything.

---

## 2. Current State (Already In Place)

The architect has created the following files in
`libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/`:

| File | Current Surface | Notes |
|---|---|---|
| `IApplication.cs` | `Task RunAsync(CancellationToken = default)` | Correct — does NOT extend `IHost` |
| `IApplicationBuilder.cs` | `AddResource(IApplicationResource)`, `AddResource(Func<IApplicationContext, IApplicationResource>)`, `Build()` | `AddResource` returns `IApplicationBuilder` — must change to return `IApplicationResourceDescriptor` |
| `IApplicationContext.cs` | `IApplicationResourceCollection Resources { get; }` | Correct — no DI/Config/Logging |
| `IApplicationEnvironment.cs` | Empty marker `interface IApplicationEnvironment;` | Reserved for future environment metadata |
| `IApplicationResource.cs` | `ResourceId Id => Guid.AsDeterministicGuid(Name)` (default impl), `ResourceName Name { get; }` | OK |
| `IApplicationResourceCollection.cs` | `: IList<IApplicationResource>` | OK |
| `IApplicationResourceDescriptor.cs` | `IApplicationResource Resource { get; }`, `IApplicationResourceDescriptor DependOs(IApplicationResourceDescriptor resource)` | **Typo: `DependOs` must be renamed to `DependsOn`** |
| `IApplicationResourcePublisher.cs` | `Task PublishAsync(CancellationToken = default)` | OK — used by `{Resource}.ApplicationModel` packages for manifest emission |
| `Extensions/ApplicationModelExtensions.cs` | empty static class | Holding place |

The csproj currently:

```xml
<CohesionProjectReference Include="Assimalign.Cohesion.Core" />
<CohesionCodeGenValueType Include="ValueTypes\ResourceName.cs" ObjectRuntimeType="String" .../>
<CohesionCodeGenValueType Include="ValueTypes\ResourceId.cs" ObjectRuntimeType="Guid" .../>
```

So `ResourceId` (Guid wrapper) and `ResourceName` (String wrapper) are emitted by the existing `CohesionCodeGenValueType` codegen pipeline and the package depends *only* on `Assimalign.Cohesion.Core`. **Both invariants are preserved by this plan.**

---

## 3. Refined Architecture

```
┌ Assimalign.Cohesion.{Libraries}                              [Layer 0]
├ Assimalign.Cohesion.ApplicationModel                         [Layer 1]
├───── Assimalign.Cohesion.ApplicationModel.Gateway            [Layer 2a]
├───── Assimalign.Cohesion.ApplicationModel.Gateway.{Platform} [Layer 2b]
└────┬ Assimalign.Cohesion.{Resource}                          [Layer 3a — base contracts]
     ├ Assimalign.Cohesion.{Resource}.{Feature}                [Layer 3b — extends base]
     ├ Assimalign.Cohesion.{Resource}.Application              [Layer 3c — standalone exe]
     └ Assimalign.Cohesion.{Resource}.ApplicationModel         [Layer 3d — manifest gen]
```

### Layer Responsibilities

- **Layer 0 — Libraries** (`Core`, `DependencyInjection`, `Configuration`, `Logging`, `Hosting`, `FileSystem`, `Http`, `Net`, …)
  Domain-agnostic primitives. ApplicationModel itself only references `Core`.

- **Layer 1 — `ApplicationModel`**
  Pure orchestration contracts: `IApplication`, `IApplicationBuilder`, `IApplicationContext`, `IApplicationResource`, `IApplicationResourceCollection`, `IApplicationResourceDescriptor`, `IApplicationGateway`, `IApplicationResourcePublisher`. Plus the `Application` static factory and a default builder/context/descriptor implementation. **Knows nothing about hosting, DI, configuration, or logging.**

- **Layer 2a — `ApplicationModel.Gateway`**
  Base gateway contract elaborations + the `LocalGateway` implementation (default for local development). LocalGateway resolves each `IApplicationResource` to a sibling `{Resource}.Application` executable, spawns it as a child process, supervises it, and orders startup by descriptor `DependsOn` chains.

- **Layer 2b — `ApplicationModel.Gateway.{Platform}`**
  Concrete platform-specific gateways: `Gateway.Kubernetes`, `Gateway.Docker`, `Gateway.AzureContainerApps`, … Each one consumes the resource graph from `IApplicationContext` and translates it into platform-native orchestration calls (Kube API, Docker socket, ACA REST). **Platforms dictate the hosting model.**

- **Layer 3a — `{Resource}` (base)**
  Pure domain contracts (e.g. `IScheduler`, `ISchedule`, `IScheduleProvider`). No hosting, no DI, no Application concept. Currently this is `Assimalign.Cohesion.Scheduler`, `Assimalign.Cohesion.Web`, etc.

- **Layer 3b — `{Resource}.{Feature}`**
  Cross-cutting extensions internal to a resource (e.g. `Scheduler.Cron`, `Scheduler.Timer`, `Web.Cors`, `Web.Authentication`). They depend only on the base `{Resource}` package. They are integrated **only** at the `{Resource}.Application` level — the ApplicationModel manifest does not know about them. They are hidden runtime details composed inside the running `.Application` process.

- **Layer 3c — `{Resource}.Application` (standalone executable)**
  The package formerly named `{Resource}.Hosting`. Owns the in-process composition: `ServiceProviderBuilder`, `ConfigurationManager`, `HostEnvironment`, lifecycle, `Host<TContext>` extension. Builds a `Program.cs`-style entry point. **Does NOT depend on `Assimalign.Cohesion.ApplicationModel`.** It is a runnable binary independent of the orchestration layer.

- **Layer 3d — `{Resource}.ApplicationModel` (manifest generator)**
  Provides the consumer-facing `builder.Add{Resource}(name)` extensions, an `IApplicationResource` implementation describing the resource, and an `IApplicationResourcePublisher` that emits a manifest (config block, image reference, env vars, ports, volumes) into a deployment artifact. Depends on `Assimalign.Cohesion.ApplicationModel`. Does NOT depend on `{Resource}.Application` at compile time — it knows the *executable name* by convention and validates at gateway-resolve time.

---

## 4. Core Abstractions

### 4.1 Interfaces to Keep As-Is

- `IApplication` — final shape correct.
- `IApplicationContext` — final shape correct (just `Resources`).
- `IApplicationEnvironment` — empty marker, reserved.
- `IApplicationResource` — final shape correct.
- `IApplicationResourceCollection` — final shape correct.
- `IApplicationResourcePublisher` — final shape correct.

### 4.2 Required Modifications To Existing Files

**File:** `IApplicationResourceDescriptor.cs`
Rename method `DependOs` → `DependsOn`. Add overload accepting a `params IApplicationResourceDescriptor[]` for ergonomics. Add a `Dependencies` accessor required by the gateway for topological ordering. Final shape:

```csharp
public interface IApplicationResourceDescriptor
{
    IApplicationResource Resource { get; }
    IReadOnlyList<IApplicationResourceDescriptor> Dependencies { get; }
    IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource);
    IApplicationResourceDescriptor DependsOn(params IApplicationResourceDescriptor[] resources);
}
```

**File:** `IApplicationBuilder.cs`
Both `AddResource` overloads return `IApplicationResourceDescriptor` (currently they return `IApplicationBuilder`, which makes the consumer-facing fluent `DependsOn` chain impossible). Add `UseGateway`. Final shape:

```csharp
public interface IApplicationBuilder
{
    IApplicationResourceDescriptor AddResource(IApplicationResource resource);
    IApplicationResourceDescriptor AddResource(Func<IApplicationContext, IApplicationResource> configure);
    IApplicationBuilder UseGateway(IApplicationGateway gateway);
    IApplication Build();
}
```

`AddResource(...)` returning the descriptor enables the consumer pattern:
`builder.AddIdentityHub("IdentityHub").DependsOn(dns);`

`UseGateway(...)` is fluent (returns the builder) so the consumer can do:
`builder.UseGateway(new LocalGateway()).AddResource(...)`.
If never called, `Build()` resolves a default gateway via lookup (see §5).

**File:** `Extensions/ApplicationModelExtensions.cs`
Will host the optional-but-typical `UseGateway<TGateway>()` generic helper and any `IApplicationContext` query helpers (`GetResource<T>(name)`, `TopologicalOrder()`).

### 4.3 New Abstractions To Add (Layer 1)

**`IApplicationGateway` — `src/Abstractions/IApplicationGateway.cs`**

```csharp
public interface IApplicationGateway
{
    string Name { get; }
    Task StartAsync(IApplicationContext context, CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
```

Gateways own the actual execution: the local gateway spawns processes; the kubernetes gateway speaks to kube-apiserver; the docker gateway invokes the docker socket. The `IApplicationContext` is the immutable resource graph the gateway must realize.

### 4.4 New Static Factory

**`Application` — `src/Application.cs`**

```csharp
public static class Application
{
    public static IApplicationBuilder CreateBuilder() => new ApplicationBuilder();
    public static IApplicationBuilder CreateBuilder(string[] args) => new ApplicationBuilder(args);
}
```

Mirrors the `WebApplication.CreateBuilder()` idiom and lets the consumer write
`IApplicationBuilder builder = Application.CreateBuilder();`.

### 4.5 New Internal Implementations (Layer 1)

All under `src/Internal/`:

- `ApplicationBuilder.cs` — the only `IApplicationBuilder` implementation. Maintains a working `ApplicationResourceCollection`, a list of `IApplicationResourceDescriptor`, and a current `IApplicationGateway`. `Build()` constructs an `ApplicationContext`, validates the descriptor graph (no cycles, all dependencies present, names unique) and returns a `CohesionApplication`.
- `ApplicationContext.cs` — implements `IApplicationContext` with a populated `IApplicationResourceCollection`. Holds the `IApplicationGateway` chosen at build time.
- `ApplicationResourceCollection.cs` — backing `List<IApplicationResource>` with name-uniqueness validation on `Add/Insert`.
- `ApplicationResourceDescriptor.cs` — internal descriptor; tracks the resource and its `DependsOn` graph.
- `CohesionApplication.cs` — the only `IApplication` implementation. Takes the built `ApplicationContext` + chosen `IApplicationGateway`. `RunAsync(ct)` does:
  1. `await gateway.StartAsync(context, ct)`
  2. Wait for `ct` cancellation (linked TCS, mirrors the `Host<TContext>.RunAsync` pattern)
  3. `await gateway.StopAsync(stopCt)` with the configured shutdown timeout

**Default gateway resolution (when `UseGateway` not called):** `ApplicationBuilder.Build()` attempts to discover a registered gateway in this order:
1. Type loaded from a known assembly name `Assimalign.Cohesion.ApplicationModel.Gateway` exposing `LocalGateway`.
2. Throw `InvalidOperationException("No IApplicationGateway registered. Add a UseGateway() call or reference Assimalign.Cohesion.ApplicationModel.Gateway.")`

This keeps the Layer 1 package free of any gateway dependency while still giving the consumer one-line ergonomics when they reference the Gateway package.

---

## 5. Gateway Pattern

### 5.1 Layer 2a — `Assimalign.Cohesion.ApplicationModel.Gateway`

New project: `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/Assimalign.Cohesion.ApplicationModel.Gateway.csproj`. References `Assimalign.Cohesion.ApplicationModel`.

Contents:
- `Abstractions/IApplicationGatewayResolver.cs` — resolves `IApplicationResource` → executable path / image reference.
- `LocalGateway.cs` — default `IApplicationGateway` implementation.
- `LocalGatewayResolver.cs` — default resolver: looks up `{ResourceTypeAssembly}.Application.exe` adjacent to the orchestrator binary, falling back to `dotnet run` against a referenced project in dev environments.
- `LocalGatewayProcessSupervisor.cs` — manages spawned `Process` lifetimes, restart policy, log piping.
- `Extensions/LocalGatewayExtensions.cs` — `UseLocalGateway()` extension on `IApplicationBuilder`.

#### LocalGateway resource resolution strategy

1. `StartAsync(context, ct)` reads `context.Resources` and the descriptor graph.
2. Compute topological order from `IApplicationResourceDescriptor.Dependencies`. Cycles fail fast.
3. For each resource, resolver maps `IApplicationResource` (which carries strongly-typed metadata such as a published `ApplicationExecutable` hint or a conventional `{ResourceType}.Application` assembly name) to a concrete process invocation.
4. Spawn child processes in dependency order. Wait for each process's "ready" signal (default: stdout marker, configurable to a port-listen probe or a named-pipe handshake).
5. Pipe child stdout/stderr to the orchestrator console with a `[ResourceName]` prefix.
6. On `StopAsync`: send SIGTERM (Ctrl+C on Windows) in reverse topological order, with a per-resource grace period before SIGKILL.

LocalGateway is the *default for local dev* and lives in the base Gateway package precisely because it requires zero platform tooling.

### 5.2 Layer 2b — `Assimalign.Cohesion.ApplicationModel.Gateway.{Platform}`

Each platform package is its own project under `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.{Platform}/`. They reference `Assimalign.Cohesion.ApplicationModel.Gateway` (so they can reuse the resolver abstraction and base extension shapes).

Examples:
- `Gateway.Kubernetes` — translates the resource graph to `Deployment` + `Service` + `ConfigMap` objects, applies via the official `KubernetesClient` SDK (already version-pinned in `build/Targets/PackageReferences.targets`), watches readiness, on `StopAsync` deletes the namespace it created.
- `Gateway.Docker` — translates to `docker run` invocations against the local docker socket. Useful for integration test environments.
- `Gateway.AzureContainerApps` (later) — translates to ACA REST API calls.

The platform dictates the hosting model end-to-end: the base ApplicationModel makes no assumption about whether resources run as processes, containers, pods, or serverless functions.

---

## 6. Resource Patterns (Concrete Shapes for Scheduler)

### 6.1 Layer 3a — `Assimalign.Cohesion.Scheduler` (base contracts, already exists)

Pure domain. No changes from this plan.

```csharp
public interface IScheduler
{
    Task StartAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}
public interface ISchedule { /* ... */ }
public interface IScheduleProvider { /* ... */ }
```

### 6.2 Layer 3b — `Assimalign.Cohesion.Scheduler.Cron` and `.Timer` (already exist)

Already factored. No changes.

### 6.3 Layer 3c — `Assimalign.Cohesion.Scheduler.Application` (renamed from `.Hosting`)

This is a direct rename of `Assimalign.Cohesion.Scheduler.Hosting` → `Assimalign.Cohesion.Scheduler.Application`. **No code changes inside the project for the rename itself**; it remains a `Host<ScheduleContext>` based standalone executable that composes DI/Config/Logging using the existing `Host<TContext>` machinery from `libraries/Hosting/Assimalign.Cohesion.Hosting/src/Implementation/Host.TContext.cs`. The class names `ScheduleApplication`, `ScheduleApplicationBuilder`, `ScheduleContext`, `ScheduleOptions` already align with the new naming — only the project / package / namespace folder is renamed.

Code shape (current, preserved post-rename):

```csharp
// Assimalign.Cohesion.Scheduler.Application
public class ScheduleApplication : Host<ScheduleContext>, IScheduler { ... }

public class ScheduleApplicationBuilder : ISchedulerBuilder
{
    public ServiceProviderBuilder Services { get; }
    public ConfigurationManager Configuration { get; }
    public FileSystemFactoryBuilder FileSystems { get; }
    public HostEnvironment Environment { get; }
    public Task<IScheduler> BuildAsync() { ... }
}
```

The package's entry-point is its own `Program.cs` (a thin Main that calls `await ScheduleApplication.Run(args)`). It is independently runnable as `Assimalign.Cohesion.Scheduler.Application.exe` — which is exactly what the LocalGateway resolves to.

**Critical: `Scheduler.Application` does NOT reference `Assimalign.Cohesion.ApplicationModel`.** It is the runnable artifact. The orchestration layer is upstream.

### 6.4 Layer 3d — `Assimalign.Cohesion.Scheduler.ApplicationModel` (NEW)

New project. Provides three pieces:

```csharp
// SchedulerResource.cs
public sealed class SchedulerResource : IApplicationResource
{
    public ResourceName Name { get; }
    public string ApplicationExecutable => "Assimalign.Cohesion.Scheduler.Application";
    public SchedulerResource(string name) { Name = name; }
}

// SchedulerResourcePublisher.cs
public sealed class SchedulerResourcePublisher : IApplicationResourcePublisher { ... }

// Extensions/SchedulerApplicationModelExtensions.cs
public static class SchedulerApplicationModelExtensions
{
    public static IApplicationResourceDescriptor AddScheduler(
        this IApplicationBuilder builder,
        string name)
        => builder.AddResource(new SchedulerResource(name));
}
```

References: `Assimalign.Cohesion.ApplicationModel` only. **Does NOT reference `Scheduler.Application`** — it knows the executable name by convention, and the gateway validates resolution at runtime.

---

## 7. Consumer Experience

### 7.1 Multi-resource orchestration (the architect's example)

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Application.CreateBuilder();

IApplicationResourceDescriptor dns         = builder.AddDns("");
IApplicationResourceDescriptor identityHub = builder.AddIdentityHub("IdentityHub").DependsOn(dns);
IApplicationResourceDescriptor adminWebApp = builder.AddWebApp("Administration").DependsOn(identityHub);
IApplicationResourceDescriptor usersWebApp = builder.AddWebApp("Users").DependsOn(identityHub);
IApplicationResourceDescriptor employees   = builder.AddWebApp("Employees").DependsOn(identityHub);

IApplication app = builder.Build();
await app.RunAsync();
```

Project references on the orchestrator host (e.g. `MyCompany.Platform.Host.csproj`):
- `Assimalign.Cohesion.ApplicationModel`
- `Assimalign.Cohesion.ApplicationModel.Gateway` (or any `Gateway.{Platform}` for non-local)
- `Assimalign.Cohesion.Dns.ApplicationModel`
- `Assimalign.Cohesion.IdentityHub.ApplicationModel`
- `Assimalign.Cohesion.Web.ApplicationModel`

Run `dotnet run` and LocalGateway spawns five child processes in dependency order, supervises them, and forwards SIGINT.

### 7.2 Standalone resource

A resource can also run by itself with no ApplicationModel involvement at all:

```csharp
// Program.cs in a custom Scheduler app
using Assimalign.Cohesion.Scheduler;

ISchedulerBuilder builder = new ScheduleApplicationBuilder();
builder.Services.AddSingleton<IFooService, FooService>();
builder.AddSchedule(new MySchedule());

IScheduler scheduler = await builder.BuildAsync();
await scheduler.StartAsync();
```

This is the same surface a developer uses today and is what the LocalGateway eventually invokes per resource.

---

## 8. MSBuild SDK Integration

### 8.1 csproj item type

Add a new MSBuild item type `CohesionResourceManifest` consumed by `{Resource}.ApplicationModel` projects:

```xml
<ItemGroup>
  <CohesionResourceManifest Include="ResourceManifest\SchedulerResource.cs"
                            ResourceType="Scheduler"
                            ApplicationExecutable="Assimalign.Cohesion.Scheduler.Application"
                            DefaultPort="5085"
                            ObjectNamespace="Assimalign.Cohesion.Scheduler.ApplicationModel"
                            ObjectAccessModifier="public" />
</ItemGroup>
```

### 8.2 New MSBuild task — `CreateResourceManifestTask`

New file: `sdks/Assimalign.Cohesion.Sdk/Tasks/Tasks/CodeGeneration/CodeGenerationTask.CreateResourceManifest.cs`. Mirrors the existing `CreateValueTypeTask` pattern at `sdks/Assimalign.Cohesion.Sdk/Tasks/Tasks/CodeGeneration/CodeGenerationTask.CreateValueType.cs`:

- Inherits `CodeGenerationTask`.
- Reads `CohesionResourceManifest` items.
- For each item, emits a `partial class` (matching the `Include` path) implementing `IApplicationResource` with the configured metadata baked in as constants.
- Optionally emits an extension class `Add{ResourceType}(this IApplicationBuilder builder, string name)` so the architect-side hand-written extension can be elided.

The build wires the task in `Build.targets` next to `CohesionCodeGenValueType` so it runs in the same `BeforeCompile` slot.

### 8.3 Convention

Each `{Resource}.ApplicationModel` csproj declares one `CohesionResourceManifest` per resource type it exposes. The codegen pipeline emits the boilerplate, the human-authored code in the project provides only behavior overrides.

---

## 9. Migration Plan (Phased)

### Phase 1 — Complete core ApplicationModel abstractions (Layer 1)

- Apply `DependOs` → `DependsOn` rename and add `Dependencies` accessor on `IApplicationResourceDescriptor`.
- Change `IApplicationBuilder.AddResource(...)` return type to `IApplicationResourceDescriptor`; add `UseGateway`.
- Add `IApplicationGateway`.
- Add `Application` static factory.
- Add `Internal/ApplicationBuilder`, `ApplicationContext`, `ApplicationResourceCollection`, `ApplicationResourceDescriptor`, `CohesionApplication`.
- Add unit tests under `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/tests/`.

### Phase 2 — Gateway base package + LocalGateway (Layer 2a)

- Create `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/` project.
- Implement `LocalGateway`, `LocalGatewayResolver`, `LocalGatewayProcessSupervisor`.
- `UseLocalGateway()` extension.
- Wire it as the discovered default in `ApplicationBuilder`.

### Phase 3 — Rename `Scheduler.Hosting` → `Scheduler.Application` (proof of concept)

- Pure rename — directory, slnx entry, csproj name, namespace folder. `git mv` preserves history.
- No code changes inside the project.
- Add a `Program.cs` if missing so the project produces an executable.
- Update any consumer slnx that referenced `Scheduler.Hosting`.

### Phase 4 — Create `Scheduler.ApplicationModel` (Layer 3d)

- New project `resources/Scheduler/Assimalign.Cohesion.Scheduler.ApplicationModel/`.
- Add `SchedulerResource`, `SchedulerResourcePublisher`, `Extensions/SchedulerApplicationModelExtensions.cs`.
- Wire `CohesionResourceManifest` once the SDK task lands (or hand-author the file in the meantime).
- Integration test: `Application.CreateBuilder().AddScheduler("foo").Build().RunAsync()` actually starts the renamed `Scheduler.Application` exe via LocalGateway.

### Phase 5 — Repeat for `Web`

- Resolve the dual-implementation conflict: `resources/Web/Assimalign.Cohesion.Web.ApplicationModel/` already exists with an OLD competing implementation (a self-hosting `WebApplication` that tried to be both manifest *and* runtime). **Delete the existing src tree** of that project and rebuild it as a Layer 3d manifest-only project mirroring `Scheduler.ApplicationModel`.
- Rename `resources/Web/Assimalign.Cohesion.Web.Hosting/` → `Assimalign.Cohesion.Web.Application/`.
- Add `WebResource`, `WebResourcePublisher`, `AddWebApp(...)` extension.

### Phase 6 — Platform Gateways (Layer 2b)

- Start with `Gateway.Kubernetes` — the highest-leverage non-local target.
- Create `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes/`.
- `KubernetesGateway` implementing `IApplicationGateway`, plus a `KubernetesResourceTranslator` per resource type discovered via assembly scan.

### Phase 7 — MSBuild codegen rollout

- Implement `CreateResourceManifestTask` mirroring `CreateValueTypeTask`.
- Wire into `build/Targets/` and `sdks/Assimalign.Cohesion.Sdk/`.
- Convert hand-written `{Resource}.ApplicationModel` resource classes to codegen.

---

## 10. Files To Create / Modify / Delete

### Modifications to existing abstraction files

- `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/IApplicationResourceDescriptor.cs` — rename `DependOs` → `DependsOn`; add `Dependencies` accessor; add `params` overload.
- `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/IApplicationBuilder.cs` — change `AddResource` return type to `IApplicationResourceDescriptor`; add `UseGateway`.
- `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Extensions/ApplicationModelExtensions.cs` — add `UseGateway<TGateway>()` helper, `GetResource<T>(name)`, `TopologicalOrder()` query helpers.

### New files in Layer 1 (`Assimalign.Cohesion.ApplicationModel`)

- `src/Abstractions/IApplicationGateway.cs`
- `src/Application.cs`
- `src/Internal/ApplicationBuilder.cs`
- `src/Internal/ApplicationContext.cs`
- `src/Internal/ApplicationResourceCollection.cs`
- `src/Internal/ApplicationResourceDescriptor.cs`
- `src/Internal/CohesionApplication.cs`
- `src/Internal/GatewayResolver.cs` (reflection-based default-gateway probe)
- `tests/ApplicationBuilderTests.cs`
- `tests/ApplicationResourceDescriptorTests.cs`

### New files in Layer 2a (`Assimalign.Cohesion.ApplicationModel.Gateway`)

- `src/Assimalign.Cohesion.ApplicationModel.Gateway.csproj`
- `src/LocalGateway.cs`
- `src/LocalGatewayResolver.cs`
- `src/LocalGatewayProcessSupervisor.cs`
- `src/Extensions/LocalGatewayExtensions.cs`
- `tests/LocalGatewayTests.cs`

### Renames

- `resources/Scheduler/Assimalign.Cohesion.Scheduler.Hosting/` → `resources/Scheduler/Assimalign.Cohesion.Scheduler.Application/` (and slnx + csproj + namespace folder updates).
- `resources/Web/Assimalign.Cohesion.Web.Hosting/` → `resources/Web/Assimalign.Cohesion.Web.Application/`.

### New files for `Scheduler.ApplicationModel`

- `resources/Scheduler/Assimalign.Cohesion.Scheduler.ApplicationModel/src/Assimalign.Cohesion.Scheduler.ApplicationModel.csproj`
- `resources/Scheduler/Assimalign.Cohesion.Scheduler.ApplicationModel/src/SchedulerResource.cs`
- `resources/Scheduler/Assimalign.Cohesion.Scheduler.ApplicationModel/src/SchedulerResourcePublisher.cs`
- `resources/Scheduler/Assimalign.Cohesion.Scheduler.ApplicationModel/src/Extensions/SchedulerApplicationModelExtensions.cs`

### Deletions

- All current contents under `resources/Web/Assimalign.Cohesion.Web.ApplicationModel/src/` (the broken competing implementation that tried to be both manifest *and* runtime — `WebApplication.cs`, `WebApplicationBuilder.cs`, `WebApplicationOptions.cs`, the `Server/` folder, the `Internal/` folder). The csproj is preserved and rebuilt as a Layer 3d manifest-only project.

### MSBuild SDK

- `sdks/Assimalign.Cohesion.Sdk/Tasks/Tasks/CodeGeneration/CodeGenerationTask.CreateResourceManifest.cs` — new task mirroring `CreateValueTypeTask`.
- `build/Targets/` — wire `CohesionResourceManifest` item type into the codegen pipeline.

---

## 11. Open Issue Resolution — Feature Integration

The architect's design note flagged: *"How to add features without relying on the `Assimalign.Cohesion.{Resource}.Application`"*.

**Resolution per architect's clarification: features are resource-internal only.**

Implications:
- A consumer who wants `Scheduler` with `Cron` support produces a `Scheduler.Application` binary that already references `Scheduler.Cron` in its csproj. The orchestrator-side `builder.AddScheduler("foo")` does not (and cannot) configure Cron at the manifest layer.
- For deployments where features differ per environment, the team produces multiple `Scheduler.Application.{Variant}` binaries and the resource publisher emits the right `ApplicationExecutable` hint per descriptor.
- The ApplicationModel manifest contract is intentionally narrow: name, type, dependencies, exposed ports, env vars, mounts. It does not enumerate features.

This keeps ApplicationModel deployment artifacts stable across feature evolution and avoids a feature-flag matrix bleeding into the manifest schema.

---

## 12. Verification

### Unit tests

- `ApplicationBuilderTests`
  - `AddResource_ReturnsDescriptor()` — non-null, descriptor wraps the resource.
  - `Build_FailsWhenNoGateway_AndGatewayPackageNotReferenced()` — `InvalidOperationException` with the actionable message.
  - `Build_DiscoversLocalGateway_WhenPackageReferenced()` — succeeds.
  - `Build_FailsOnDuplicateResourceName()`.
  - `Build_FailsOnCircularDependency()`.
- `ApplicationResourceDescriptorTests`
  - `DependsOn_LinksDependency()`.
  - `DependsOn_Params_LinksAll()`.
  - `Dependencies_ReadOnly()`.

### Gateway tests

- `LocalGatewayTests`
  - `Resolver_FindsAdjacentExecutable()` against a temp directory layout.
  - `StartAsync_RespectsTopologicalOrder()` — recorded process-start ordering matches descriptor graph.
  - `StopAsync_ReverseOrder()`.
  - `StopAsync_GracePeriod_ThenSigKill()`.

### Integration test

- `EndToEndConsumerExampleTest` — execute the architect's literal consumer example using fake `Dns`, `IdentityHub`, `WebApp` resources whose `.Application` exes are simple loop-and-log programs that print a `READY` line. Assert ordered startup, ordered shutdown, exit code 0 on SIGINT.

### AOT publish verification

- Add a `publish-aot.ps1` smoke script under `scripts/` that runs `dotnet publish -p:PublishAot=true` against:
  - `Assimalign.Cohesion.ApplicationModel`
  - `Assimalign.Cohesion.ApplicationModel.Gateway`
  - `Assimalign.Cohesion.Scheduler.Application`
  - `Assimalign.Cohesion.Scheduler.ApplicationModel`
  - A sample orchestrator host
- Reflection in `GatewayResolver` is the AOT risk surface — guard with `[DynamicallyAccessedMembers]` attributes and a `RequiresUnreferencedCode` annotation, plus a `DynamicDependency` from the orchestrator app onto `LocalGateway` for the self-discovery path. CI gates publish output for trim warnings.

---

## 13. Design Decisions and Rationale

**Why ApplicationModel does not compose DI / Configuration / Logging.**
Composition is a runtime concern of *one* process. ApplicationModel describes a *graph* of processes — pulling DI/Config/Logging up to the orchestrator level would force the orchestrator to share an object graph with every resource it spawns, which is impossible across process / container / pod boundaries. Keeping composition inside `{Resource}.Application` lets each resource be independently runnable, separately versioned, and trim/AOT-friendly without dragging the orchestrator's dependency closure along. ApplicationModel's whole job is to *not* be a host.

**Why `IApplication` does not extend `IHost`.**
An `IHost` is something you run inside a process. An `IApplication` is something you describe and then ask a gateway to realize — possibly across many processes you don't own. Conflating them produces the same trap the earlier draft fell into: `.AsService()` glue, single-process assumptions, and a hosting hierarchy that fights the deployment model. Decoupling `IApplication` from `IHost` is what enables LocalGateway (multiple child processes), Gateway.Kubernetes (multiple pods), and Gateway.Docker (multiple containers) to all be valid realizations of the same `IApplication`.

**Why Gateway is a separate package.**
The base ApplicationModel package depends only on `Core` so it can be referenced from any layer of the system without dragging in process-supervision, K8s clients, or docker SDK assemblies. Gateways are platform-specific and bring heavyweight transitive dependencies; they belong outside the contract package. The reflection-based default-gateway probe in `ApplicationBuilder.Build()` keeps the consumer-facing ergonomic ("just reference the Gateway package and it works") without coupling the contract.

**Why `{Resource}.Application` does not depend on ApplicationModel.**
A Scheduler binary should be runnable standalone (developer scenario, debug-launch scenario, kube-pod-with-no-orchestrator scenario). If `{Resource}.Application` referenced `ApplicationModel`, every resource would carry the orchestration contract into every container image — a per-process tax for an out-of-process feature. Keeping the dependency edge `{Resource}.ApplicationModel → ApplicationModel` (only the manifest-emitter knows ApplicationModel) preserves the standalone-binary property.

**Why MSBuild codegen for resource manifests.**
The boilerplate for an `IApplicationResource` (carrying metadata about executable name, default port, env-var contract, etc.) is mechanical and repetitive across 18+ resource types. The existing `CohesionCodeGenValueType` pattern already proves the `<Cohesion*Codegen*>` MSBuild item-type approach; adding `CohesionResourceManifest` is a natural extension that keeps the manifest data declarative in the csproj (where deployment ops can read it) instead of buried in C# class definitions. It also lets a future `dotnet publish` target emit IaC artifacts directly from the same item metadata without reflecting over user code.

---

### Critical Files for Implementation

- `C:\Source\repos\assimalign\cohesion\libraries\ApplicationModel\Assimalign.Cohesion.ApplicationModel\src\Abstractions\IApplicationBuilder.cs`
- `C:\Source\repos\assimalign\cohesion\libraries\ApplicationModel\Assimalign.Cohesion.ApplicationModel\src\Abstractions\IApplicationResourceDescriptor.cs`
- `C:\Source\repos\assimalign\cohesion\libraries\Hosting\Assimalign.Cohesion.Hosting\src\Implementation\Host.TContext.cs` (reference pattern for `CohesionApplication.RunAsync`)
- `C:\Source\repos\assimalign\cohesion\sdks\Assimalign.Cohesion.Sdk\Tasks\Tasks\CodeGeneration\CodeGenerationTask.CreateValueType.cs` (template for `CreateResourceManifestTask`)
- `C:\Source\repos\assimalign\cohesion\resources\Scheduler\Assimalign.Cohesion.Scheduler.Hosting\src\ScheduleApplicationBuilder.cs` (the pattern to be preserved verbatim under the renamed `Scheduler.Application` package)