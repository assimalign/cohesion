# ApplicationModel — Design v3

> **Status: implemented library-family design, 2026-09-14.** This file is the
> ApplicationModel library's design. The signed [developer-experience design](../../docs/DEVELOPER_EXPERIENCE_DESIGN.md)
> is the direction of record and wins on every conflict. This v3 replaces v2.1;
> it does not establish a competing authority. Detailed contracts belong to the
> linked package designs, realization plan, and runtime contract. `[R]` retains
> an applicable v2.1 review decision; it is not a new owner approval.

## 1. Mental model: applications and the Composite

A customer's resource instance is an executable project using `Sdk.<Area>`, with a
real `Program.cs` over `<Area>Application.CreateBuilder(args)`. The framework-owned
`<Area>.Application` executable name is retired (O1). Domain content and composition
remain application code; orchestration is the per-project opt-in described in §8.

An out-of-process gateway describes and realizes resource manifests without loading
their runtimes. A **Composite** is the explicit colocation boundary: an `Sdk.Gateway`
executable in in-process mode, itself a resource with a manifest and image. Its
composition root can carry its members' runtimes. This exception belongs to the
consumer executable, never to a platform gateway or an area declarative package.

`Sdk.Gateway/Sdk/Sdk.targets` forces `CohesionApplicationModel=enabled`,
`CohesionResourceKind=Composite`, and the `Gateway.ControlPlane` resource package.
The SDK's `CohesionCreateResourceManifest.LiftComposite` lifts same-application member
endpoints and mounts, refuses Job members, and preserves the `Certificate="public"`
sentinel while prefixing named member certificates. The **base-owned composite planner**
produces its plan; no area planner owns Composite. `InProcessPlanController` compiles
the supported in-process subset, as defined by the
[InProcess package design](Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/docs/DESIGN.md).

## 2. Package ownership

| Package | Responsibility and owning design |
|---|---|
| `Assimalign.Cohesion.ApplicationModel` | Portable graph, manifest, planning, application-set, external-reference and control-plane contracts; [package design](Assimalign.Cohesion.ApplicationModel/docs/DESIGN.md). |
| `Assimalign.Cohesion.ApplicationModel.Gateway` | Shared plan-driven lifecycle, Local realization, observed state, input resolution and telemetry injection; [package design](Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md). |
| `Assimalign.Cohesion.ApplicationModel.Gateway.InProcess` | Real entry-point invocation under isolated ambient contexts, explicit admission and member lifecycle; [package design](Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/docs/DESIGN.md) (item 24, `a03cfcf8`). |
| `Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` | Authenticated discovery, observed views, commands and federation endpoints; [package design](Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane/docs/DESIGN.md) (item 23a, `1a5cce67`). |

Kubernetes and Docker gateway packages live in **cohesion-platforms**. Their compiler
implementations and platform dependencies are owned there. There is no Kubernetes
package, `KubernetesClient` dependency, or platform AOT carve-out in this repository's family.

## 3. Contract ownership and reading order

Read the signed direction first, then the [realization-plan contract](../../docs/REALIZATION_PLAN.md)
for `cohesion/plan/v1`, per-platform semantics, spec-versus-hint treatment, and O30 gates.
The [runtime contract](../../docs/RUNTIME_CONTRACT.md) owns the frozen `COHESION_*`
surface, including certificates and telemetry. Package designs own lifecycle details;
this document links to them instead of defining a second contract.

## 4. Declarative model

### 4.1 Application and builder

`IApplicationBuilder` collects desired state and validates it at `Build()`; `IApplication`
drives the selected gateway. Application identity, lifecycle policy and graph relationships
are explicit. The customer supplies the composition root and its real entry point.

### 4.2 Descriptors and resources **[R]**

Descriptors are the authoritative graph, and the model exposes resources as a read-only
projection. `IApplicationResource` supplies identity and name; manifest-backed typed
resources carry the facts a planner needs. The builder's ordered, name-unique collection
is internal; there is no separate public mutable-collection contract.

### 4.3 Manifests and plans

The SDK emits facts in `resource.json`; planners compute `ResourcePlan` at application
`Build()`, with validation before realization. The [plan document](../../docs/REALIZATION_PLAN.md)
owns the IR and planner rules. The manifest still carries `MaxReplicas`, while the plan
does not; that compiler-side validation gap remains open as B13.

### 4.4 Graph verbs

Area composition verbs ship in the feature's own package as C# 14
`extension(IApplicationBuilder)` or `extension(I<Area>ResourceDescriptor)` members.
The landed Rezolvr member is `AddRezolvr(ResourceManifest, RezolvrResourceOptions?)`,
returning `IRezolvrResourceDescriptor`, in `RezolvrResourceExtensions.cs:19`.
Descriptor-specific command verbs extend their typed descriptor. They do not move
feature contracts into the gateway or the area's hosting module.

### 4.5 Executable artifacts **[R]**

The compatibility capability `IExecutableResource.Artifact` identifies the customer's
executable artifact, not a framework-owned `Assimalign.Cohesion.Web.Application`.
Manifest artifact facts and plan intent drive current realization; §7 describes the
image build boundary without inventing runtime compilation.

### 4.7 Application sets and externals

`IApplicationSet` coordinates multiple named applications; `ExternalResourceDeclaration`
and `RemoteReference` represent application boundaries without importing foreign runtime
assemblies. The [base package design](Assimalign.Cohesion.ApplicationModel/docs/DESIGN.md)
owns those contracts (items 23/38c, `45e36dba`, `63975fcc`), and the ControlPlane design
owns authenticated remote discovery.

### 4.8 Commands

Commands are declared on descriptors, scheduled by the gateway and authorized by the
provider control plane; the [base package](Assimalign.Cohesion.ApplicationModel/docs/DESIGN.md)
and [ControlPlane package](Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane/docs/DESIGN.md)
own the contracts (items 23b/31c, `529d600b`, `81570328`). Landed wire kinds use
`<area>.<verb-noun-kebab>` and manifest `commands` remains a string array (B1–B5).

## 5. Gateway lifecycle

### 5.1 Controllers **[R]**

Controllers reconcile a planned resource and do not own the readiness wait. Explicit
registration through `ApplicationGatewayOptions.Controllers`, including the Local
plan controller, replaces reflective controller discovery; see the
[Gateway design](Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md).

### 5.2 Observed state **[R]**

The gateway observer owns observed lifecycle, health and bound endpoints. Controllers
apply desired state; dependents consume observed endpoints rather than guessed ports.
The Gateway design owns waiter, generation and reconciliation behavior.

### 5.3 `IApplicationGateway` and the readiness gate **[R]**

The shared `ApplicationGateway` algorithm validates and topologically orders the graph,
gathers artifacts without building them, starts observation, and reconciles each resource
before awaiting its initial readiness result. The planner populates
`plan.Workload.Gate = ReadinessGate(Terminals, Satisfying)` from `Workload.Kind`.
The base gate waits on `Terminals` and succeeds **iff `Satisfying` contains the reached state**.
This is landed behavior (`1391b8ba`, item 26), not an interim static-set plan.

For Deployment/StatefulSet/DaemonSet the terminal set remains **{Running, Failed, Stopped}**,
with **Running** satisfying readiness. Job completion satisfies readiness with **Stopped**.
Failure, a non-satisfying terminal, or timeout blocks/skips the dependent subtree according
to the configured failure policy. **Degraded is observed but non-gating**: it does not
satisfy initial readiness, and a later Degraded observation never re-gates already-admitted
dependents. `InitialReadinessTerminals` remains a test-fixture name; production reads
`plan.Workload.Gate.Terminals` and `plan.Workload.Gate.Satisfying`.

The [O30 gate contract](../../docs/REALIZATION_PLAN.md) and
[Gateway lifecycle design](Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md)
own precise workload and reconciliation semantics. Platform observation, apply-conflict
handling and shutdown are compiler/controller concerns; they are not Kubernetes-specific
requirements imposed on every gateway by the base algorithm.

### 5.4 Shutdown

Shutdown unwinds owned lifecycle work in dependency order. Platform **Stop is not teardown**:
it does not delete a namespace or data. Explicit deletion is a separate operation, per
cohesion-platforms rule 9 and the platform's realization contract.

## 6. Inputs, discovery and runtime capabilities

Protected Secret/Configuration inputs resolve gateway-side through the thin store clients
before mounting; the [Gateway design](Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md)
owns that boundary (`656f6325`, `280cc8c5`). Cross-resource discovery injects the observed
view (`c61e2059`), while federation uses the ControlPlane or export documents.

The certificate contract is already recorded in the signed design §5/O32 (`be26ff46`):
readers accept key-second/key-last bundles and self-signed roots, newly issued bundles
put the PKCS#8 key last, and validation is per endpoint with **no all-or-nothing certificate
invariant**. The [runtime contract](../../docs/RUNTIME_CONTRACT.md) owns its carriers.
Enrollment at the two named client seams remains deferred (B8).

Telemetry (`acc951aa`) is owned by the
[Hosting.Telemetry design](../Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md),
[OpenTelemetry design](../OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/docs/DESIGN.md),
the Gateway and InProcess designs, and the runtime contract: they define scoped log-export
credentials, injection eligibility, protocol support, and shutdown. B18–B29 record the
delivered limitations without replacing those contracts.

## 7. Platform realization and images

### 7.1 Plan compilers

This repository owns platform-neutral `ResourcePlan` (`cohesion/plan/v1`); one compiler
per platform in cohesion-platforms consumes it. Platform gateways **never reference
`<Area>.ApplicationModel`**, per COHRES003 and platforms rule 3. The
[realization-plan document](../../docs/REALIZATION_PLAN.md) owns workload topology and
spec-versus-hint handling; there is no universal single-Deployment controller.

### 7.2 Landed image contract

`sdks/Assimalign.Cohesion.Sdk/Targets/Sdk.Image.targets` produces the per-resource
`image.json`; its archive field is **`archive`**, and opted-in archives are packaged as
`cohesion/images/<file>`. Registry digest identity is the normal delivery path; the
platform-owned embedded registry is an archive fallback, not the application's registry
of record. Items 14/15 (`6499b815`, `089101b2`, `0c9e1e9c`, `535c2af1`) own this build contract.

`CohesionImageAot` selects `auto`, `true`, or `false`; COHSDK003 enforces the image AOT
requirement and COHSDK005 rejects a non-self-contained image. Image production belongs
to the SDK build, while platform compilation binds the selected registry host late.

### 7.3 Apply, observation and stop **[R]**

Each platform compiler translates the plan, its observer reports actual endpoints and
lifecycle, and its controller reconciles desired state. Kubernetes and Docker live in the
sibling repository; platform rule 9 explicitly preserves namespace/data on Stop.
The former statement that `StopAsync` deletes the namespace is withdrawn.

### 7.4 Bootstrap and render

The bootstrapper emits the gateway's own `cohesion-system` installation prerequisites;
the renderer emits compiled application resources. At the inspected committed HEAD,
both write text **without contacting the target platform**. The design's bootstrap-install
behavior belongs to item 37 and is still unlanded; B14 records that live contradiction.

## 8. SDK integration

### 8.1 Opt-in and generated surface **[R]**

`CohesionApplicationModel=enabled` generates `resource.json`, `Resource.g.cs` and
`ResourceControlPlane.g.cs` (`a2218bc1`). The default `disabled` generates none of them.
The generated control-plane registration connects the area's default factory from
`<Area>.ApplicationModel` to `ResourceRuntime` (`913b113d`, `b5dbb6ea`). The user keeps
the real `Program.cs`; none of this generates an entry point.

### 8.2 Image gather **[R]**

`CohesionPublishImages` in `Sdk.Gateway.Images.targets` is the SDK gather for project
rebuilds and pinned package images; its producer pipeline starts in `Sdk.Image.targets`.
It consumes the `image.json`/`archive` contract in §7.2. There is no
`build/Targets/Build.ApplicationModel.Containers.targets`, `application.images.json`,
or `CohesionBuildResourceContainers` gather in the landed design.

### 8.3 Generated gateway verbs

`Sdk.Gateway` generates `Gateway.CreateBuilder(args)`, resource verbs, `Externals`,
and `Applications` (`294977f7`). Build-visible resource references establish the graph;
C# composition adds explicit relationships. The generated application-set and external
surfaces use the contracts owned by the base package design.

## 9. Resource-area boundary

### 9.0 Verified inventory (18 areas, 143 src projects)

At the closing-pass baseline `67bbcf4b`, `resources/` has **18 areas**, **143 `src/`
projects**, and **18 guarded `*.ApplicationModel` packages**, each with its own planner,
typed resource and default-control-plane factory. `IApplicationResource` is implemented;
the old "implemented nowhere" inventory is obsolete. The [repository overview](../../docs/OVERVIEW.md)
lists the seven real hosts and eleven generic hosts, including LogSpace's item-31b promotion.

### 9.1 Root and features

The root supplies application and feature-composition seams; feature libraries own
their abstractions and C# 14 builder verbs. An area can decompose its root without
introducing runtime dependencies into declarative packages.

### 9.2 Hosting and testing

`<Area>.Hosting` implements the area's application builder and lifecycle over Hosting,
Resources, Health and Telemetry. Consumer executables own composition. `<Area>.Testing`
is the sole standing hosting-isolation exemption holder; non-packable `samples/`
exercise the consumer's real entry point.

### 9.4 `<Area>.ApplicationModel`: declarative plane and default control plane

Every package is AOT-compatible and **COHAM001-guarded**: manifest-backed typed resource,
platform-neutral planner, `Add<Area>(…)` graph verbs, and the **area's default-control-plane
contract/factory**. Its direct Cohesion references are `ApplicationModel` and
`Hosting.Resources` only. It never references the area's hosting runtime.

These packages and `<Area>.Client` packages are **NuGet-only**, injected by
`Sdk.<Area>` / `Sdk.Gateway`, never members of `App.<Area>`. Generated consumer code
registers the default control plane through `ResourceRuntime` when orchestration is
enabled. The [resource-area rule](../../.claude/rules/resource-areas.md) owns enforcement.

## 10. Consumer Program.cs: the Composite

This is the shipped composite template's real `Program.cs`, in an `Sdk.Gateway`
consumer configured for InProcess realization of its referenced members:

```csharp
using Assimalign.Cohesion.ApplicationModel;

IApplicationBuilder builder = Gateway.CreateBuilder(args);
builder.AddAllResources();
builder.UseGateway(args);

await builder.Build().RunAsync();
```

The project's SDK selects Composite and the default `Gateway.ControlPlane`; the
generated verbs and application/external declarations come from the referenced manifests.
Each member retains its own `Program.cs` over `<Area>Application.CreateBuilder(args)`.
`AddRezolvr` is the typed area verb described in §4.4, not `AddDns` or `AddWebApp`.
No `Resource.cs`, `Compose`, `CreateHost`, `Gateway.cs`, or `Program.g.cs` entry-point
scheme is part of this design; the signed §10 "Not created" record remains authoritative.

## 11. Build and dependency policy

Projects use `CohesionProjectReference` / `CohesionPackageReference`, central package
versions, public XML documentation, and NativeAOT-compatible implementations. DI,
configuration and logging integration belongs at the `*.Hosting` seam. The strict
declarative guard is not a blanket ban on the transitive plain-host contracts brought
by `Hosting.Resources`; the [build-system rule](../../.claude/rules/build-system.md)
lists the exact permitted closure.

## 12. Verification boundaries

Library builds and covering tests validate the portable graph and gateway contracts.
Package-consumer, container and platform end-to-end checks are separate evidence; a
documentation or library build does not prove bootstrap installation, Kubernetes
deployment, or NativeAOT publishing. Per-package designs record their own limitations.

## 13. Decisions scoped for the Composite **[R]**

The Composite is the explicit in-process composition boundary and is planned by the base
package. Its consumer can include member runtimes, while out-of-process gateways and
area declarative packages retain isolation. All examples use user-owned entry points.
Kubernetes dependencies and any JIT requirement belong to cohesion-platforms; this
repository's application-model packages retain their AOT posture. O2/O27 keep the
declarative plane NuGet-only and deliver the per-kind default control plane through it.

## 14. Questions answered **[R]**

| Former question | Landed answer |
|---|---|
| Registry host late-binding | Platform compilers bind image references after the SDK's digest/archive gather; the platform-neutral image facts stay in the plan (platforms items 34/35/36). |
| Mount provenance | Gateway-side resolution over `SecretStore.Client` and `ConfigurationStore.Client`, before materialization (`656f6325`, `280cc8c5`); Gateway design owns the input contract. |
| Controller discovery | Explicit `ApplicationGatewayOptions.Controllers` and `LocalPlanController`, with plan-derived admission (`1391b8ba`). |
| Cross-resource discovery | Observed-view injection, using realized endpoints (`c61e2059`); external application discovery uses the ControlPlane contract. |

## Build-out decisions (2026-09)

These rows record the delivered choices and open gaps from the closing build-out pass.
They match the signed design's §14 additive build-out table; they do not rewrite its
R1–R6 owner review log. Evidence paths are repository-relative unless marked sibling.
The item-37 status is based on committed platforms HEAD `5e3b1d8`; concurrent uncommitted
platform work is not a landed contract. B16 describes the committed item-34 behavior.

| # | Decision | Landed in | Where it is written |
|---|---|---|---|
| B1 | wire-kind naming = `<area>.<verb-noun-kebab>`, not design §7/§4's illustrative `<area>.<noun>` | Item 23b / 31c: `529d600b`, `81570328` | `sdks/Assimalign.Cohesion.Sdk.Rezolvr/Targets/Sdk.Rezolvr.props:18` |
| B2 | manifest `commands` stays a bare string array | Item 23b / 31c: `529d600b` | `assets/schemas/cohesion.resource.schema.json:120-126`; `ResourceManifestWriter.cs:145` |
| B3 | `cohesion.trust.add` accepted by SecretStore's control plane but deliberately absent from its SDK manifest | Item 31c / 31s: `81570328`, `c49ee153` | `resources/SecretStore/Assimalign.Cohesion.SecretStore.ApplicationModel/src/SecretStoreResourceControlPlane.cs:14`; `Sdk.SecretStore.props:19` |
| B4 | SecretStore owner = issuer for new kinds, `issuer@subject` for trust-add | Item 31c: `81570328` | `resources/SecretStore/Assimalign.Cohesion.SecretStore.Hosting/src/Internal/SecretsEndpointService.cs:448-460` |
| B5 | `--allow` enforced on apply **and** delete | Item 31c: `81570328` | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane/src/Internal/GatewayControlPlaneServer.cs:596-601,678-683` |
| B6 | Rezolvr records under `ResourceContext.GetMount("data", …)` — no Volume mount, because `GenericPlanner` requires StatefulSet for Volume mounts while `RezolvrPlanner` requires Deployment | Item 31c: `81570328` | `resources/Rezolvr/Assimalign.Cohesion.Rezolvr.Hosting/src/RezolvrApplicationBuilder.cs:69`; `RezolvrPlanner.cs`; `GenericPlanner.cs` |
| B7 | certificate contract O32 — already written by 31t; **verify and cross-link**, incl. the R-B reading-order correction and **no all-or-nothing `certificate` invariant** | Item 31t: `be26ff46` | `docs/DEVELOPER_EXPERIENCE_DESIGN.md` §5/O32; `docs/RUNTIME_CONTRACT.md` certificate rows (unchanged) |
| B8 | `Enroll` deferred at the client seams `ISecretStoreClient.EnrollIntermediateAsync` / `IGatewayStoreClient.EnrollIntermediateAsync` | Item 31t: `be26ff46` | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/Abstractions/IGatewayStoreClient.cs`; no `EnrollIntermediate` C# implementation |
| B9 | ConfigurationStore planner: "exactly one Volume named `data`; Secret/Configuration mounts beside it" | Item 31t follow-up: `5bfa8f71` | `resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore.ApplicationModel/src/Internal/ConfigurationStorePlanner.cs:46-66` |
| B10 | design item 6's deletion of `NameOnly.ProjectReference.targets` is **stale** — the load-bearing converter is kept | Item 6: `5412480c` | `5412480c` commit body; `sdks/Assimalign.Cohesion.Sdk/Sdk/Sdk.targets:22` |
| B11 | **telemetry contract (umbrella) — expanded into B18–B29** | Item 31b: `acc951aa` | `libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md`; B18–B29 below |
| B12 | the `login` credential-file shape proposed by item 40 — `~/.cohesion/credentials/<issuer-host>.json` holding `{access_token, token_type, expires_at, issuer}` — is **pinned** | Item 40: `24cf8d32` | `tooling/Cli/Assimalign.Cohesion.Cli/src/Internal/DeviceLogin.cs:128-132`; `tests/DeviceLoginTests.cs:97-101` |
| B13 | `ResourcePlan` carries no `MaxReplicas` — **record as open** | Item 38c: `27cb1822` | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Planning/ResourcePlanValidator.cs:75-79`; limit exists only on the manifest |
| B14 | `IApplicationGatewayBootstrapper` and `IApplicationGatewayRenderer` carry near-identical summaries; the bootstrapper's must say what it emits (**the gateway's own `cohesion-system` installation**, not the application's resources) — **and the design's `--mode bootstrap` *applies* semantics is item 37, still pending** | Item 27 (this item) + 37 open: XML correction in this pass; item 37 unlanded | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/IApplicationGatewayBootstrapper.cs:8-12`; `src/Internal/CohesionApplication.cs:59-67`; platforms HEAD `5e3b1d8` |
| B15 | `IApplicationGatewayRenderer`'s doc is the render-only one — correct as written, leave it | Item 27 (this item): Verified unchanged in this pass | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/IApplicationGatewayRenderer.cs:8-11` |
| B16 | Kubernetes restart-policy clamp — `Never` for `Job`, `Always` otherwise | Item 34 (not 37): platforms `c004e57` | Committed platforms `c004e57`: `platforms/Kubernetes/Assimalign.Cohesion.ApplicationModel.Gateway.Kubernetes/src/Internal/KubernetesPlanCompiler.cs:1132` (read-only snapshot) |
| B17 | platforms rule pointers — **read-only**, never edit the sibling repository | Item 33 / 34 / 36: platforms `2f6e4e7` | Sibling `.claude/rules/platform-areas.md` rules 3, 9, 10 and COHPLT001 |
| B18 | telemetry export is **OTLP/HTTP JSON only** — `OtlpProtocol` has exactly one member, `HttpJson` | Item 31b: `acc951aa` | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpProtocol.cs:4-8` |
| B19 | `otlp-grpc` is **reserved but refused** at bootstrap with a named message; `otlp-http` is the only served value | Item 31b: `acc951aa` | `libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/src/ResourceTelemetry.cs:135-142`; `tests/ResourceTelemetryTests.cs:65-74` |
| B20 | **protobuf deferred; traces and metrics deferred** because `libraries/Logging` exposes no span or instrument primitive | Item 31b: `acc951aa` | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpSignal.cs:4-12`; package `docs/DESIGN.md` |
| B21 | LogSpace's private `otlp` endpoint moved from **grpc/4317 → https/4318 with `Certificate="tls"`**, with the planner, SDK assertion, manifest fixture and template changed to match | Item 31b: `acc951aa` (+ fixture `67bbcf4b`) | `sdks/Assimalign.Cohesion.Sdk.LogSpace/Targets/Sdk.LogSpace.props:18`; `LogSpacePlanner.cs:27`; `LogSpaceResourceTests.cs:120` |
| B22 | telemetry tokens carry `aud` = LogSpace resource name, `sub` = emitting resource name, **`scope=telemetry`**, cached under a **distinct key `(application, sink, emitter)`**, separate from LogSpace's own bootstrap credential; ingest requires the scope, query/management routes reject it | Item 31b: `acc951aa` | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/ApplicationGateway.Telemetry.cs:9-25`; `src/Internal/ApplicationTrustState.cs:81`; `LogSpaceTokenVerifier.cs:75,116-125` |
| B23 | every host registers `TelemetryHostService` **first so it stops last**; its `StopAsync` flushes within **5 seconds** and the host cancellation budget, then tears the exporter down | Item 31b: `acc951aa` | `libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/src/Internal/TelemetryHostService.cs:21-44`; all 18 resource application builders |
| B24 | **`RemoteReference` telemetry injection skipped** — `IControlPlaneExternalResourceResolver` exposes no authenticated named-OTLP-endpoint contract | Item 31b: `acc951aa` | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Abstractions/IControlPlaneExternalResourceResolver.cs`; Hosting.Telemetry `docs/DESIGN.md` |
| B25 | **no inferred `DependsOn`** — a producer prepared before LogSpace is `Running` gets no telemetry injection, and an already-running process needs a restart to observe changed environment | Item 31b: `acc951aa` | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/docs/DESIGN.md` telemetry; Gateway.InProcess `docs/DESIGN.md` |
| B26 | **no live protected-header refresh** — running exporters capture headers at bootstrap and need a restart before credential expiry; the gateway credential default is **24 h** | Item 31b: `acc951aa` | `libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md`; Gateway `ApplicationGatewayOptions.cs` |
| B27 | `resources/LogSpace/Assimalign.Cohesion.LogSpace.Telemetry` stays an **empty placeholder** whose name collides with `Assimalign.Cohesion.Hosting.Telemetry` — **record as open** | Item 31b: `acc951aa` | `resources/LogSpace/README.md:12`; `Assimalign.Cohesion.LogSpace.Telemetry/src/` has no C# source |
| B28 | `docs/RUNTIME_CONTRACT.md` keeps `otlp-grpc` **contract-valid**, now qualified in-row with the refusal; the design document's own protocol lists are untouched | Item 31b: `acc951aa` | `docs/RUNTIME_CONTRACT.md:38-40` (unchanged) |
| B29 | the OTLP full-acceptance response returns an **empty `partialSuccess`** where upstream OTLP omits the field on full success — a documented, deliberate difference | Item 31b: `acc951aa` | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/docs/DESIGN.md:36`; `resources/LogSpace/Assimalign.Cohesion.LogSpace.Hosting/src/Internal/LogSpaceHttp.cs:73` |
