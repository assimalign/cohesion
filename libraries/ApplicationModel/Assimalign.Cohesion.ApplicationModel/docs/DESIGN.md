# Assimalign.Cohesion.ApplicationModel — DESIGN

> This is the per-library design record for the **Layer 1** ApplicationModel
> contract package. The current cross-package direction of record is
> [`DEVELOPER_EXPERIENCE_DESIGN.md`](../../../../docs/DEVELOPER_EXPERIENCE_DESIGN.md);
> the ApplicationModel area-root `../../DESIGN.md` is an older plan. Read this file
> for the contracts that are implemented in this package and why they have this shape.

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
  `IApplicationEnvironment`; plus the application-boundary contracts
  `ExternalResourceDeclaration`, `IExternalResource`, `IExternalResourceResolver`,
  and `RemoteReferenceOptions`. The older `IExecutableResource` /
  `IEndpointResource` / `IMountResource` capability interfaces remain as a
  compatibility surface for gateways and hand-written resources while resource
  areas move to generated manifests and plans.
- **Control plane** (contracts only; implementations live in the `…Gateway.*`
  packages) — `IApplicationGateway`, `IApplicationResourceController`,
  `IResourceControlContext`, `IApplicationResourceStateManager`,
  `IApplicationResourcePackager`, `IControlPlaneClient`,
  `IMultiModelApplicationGateway`, and the `IResourceArtifact` family.

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

`CohesionApplicationAttribute` records the SDK-selected application name in gateway assembly
metadata for build and tooling inspection. It is not a runtime discovery mechanism: generated
gateway code supplies the same identity directly to `Application.CreateBuilder(ApplicationName,
args)`, preserving the package's no-reflection boundary.

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
  working collection lives only on the builder. At `Build()`, required same-application
  manifest references infer edges by exact application/resource identity; explicit C#
  `DependsOn` edges remain ordering-only and additive. Optional references never infer a
  gating edge, and cross-application references remain external. Surfacing a mutable `IList`
  on an "immutable desired state" was a contradiction that an early review caught. A built
  descriptor also carries its immutable `Plan`; an authoring descriptor has no plan until
  `Build()` completes planning.
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
- **Readiness is a level-triggered, plan-gate wait.**
  `IApplicationResourceStateManager.WaitForStateAsync` completes on **any** state in
  `descriptor.Plan.Workload.Gate.Terminals` with a per-resource budget and succeeds
  exactly when `Gate.Satisfying` contains the reached state. Long-running workloads
  satisfy on `Running`; a Job satisfies on `Stopped`, while `Stopped` remains a fast
  failure for every non-Job kind. `Degraded` is observed but is
  non-gating: it does not admit dependents initially and never re-gates them
  after initial readiness admitted them. A timeout returns the last observed state;
  caller cancellation throws `OperationCanceledException` and removes the waiter.
  The lifecycle enum is treated as a membership set, never an ordered lattice.
- **Controllers are plan-selected, level-triggered reconcilers.** `Build()` calls gateway
  `Validate(model)`, which asks registered overrides and then the platform controller
  `CanRealize(plan, out reason)` before any artifact gather. `ReconcileAsync` computes
  desired objects and applies them, idempotently, and returns; it does not own
  steady-state observation (that is a gateway's single informer) and does not block
  on readiness (the gateway gates on the state manager).
- **Typed artifacts, no discriminator downcasts.** `IResourceArtifact` is refined by
  `IExecutableArtifact` / `IContainerImageArtifact`; consumers request the concrete
  shape by type rather than switching on a kind enum and casting.

## External references and exported models

An application-boundary reference is represented by an ordinary resource node, not by a side
table. `ExternalResourceDeclaration` snapshots the target application, consumed endpoint names,
optionality, embedded manifest, and available same-application closure. Its `ManifestHash` and
`ClosureHash` are computed from the portable canonical manifest contract, excluding local paths
and other machine-specific artifact facts.

Generated gateway code may register a declaration through the infrastructure-only
`IApplicationBuilder.AddExternal` seam. Application code binds it with
`RemoteReference(ExternalResourceDeclaration, ...)`; the string overload creates a manifest-less
declaration. Both return the same `IApplicationResourceDescriptor` used by `DependsOn`, so the
external participates in topological ordering, readiness, and observed-endpoint injection like
any locally realized resource.

A manifest-less declaration has no build-time application identity to authenticate. Its selected
file or gateway binding is therefore the authority; resolution still requires the named resource
to be present in the export and in that export's embedded model. Manifest-backed declarations
add the stronger owner and canonical-hash checks.

The base bindings are static endpoints, an `export.json` file, a peer gateway address, or an
arbitrary `IExternalResourceResolver`. The effective resolver is selected in this order:

1. matching `--external` command-line binding;
2. matching `Cohesion__External__...` process-environment binding (colon aliases are accepted);
3. the resolver supplied by `RemoteReference`;
4. the unresolved resolver.

This makes deployment/environment input authoritative over source while leaving the C# binding
as a useful local default. A peer-gateway binding uses the transport-neutral
`IControlPlaneClient`; this package deliberately does not take an HTTP dependency. A
platform-specific importer can be supplied through `RemoteReferenceOptions.Bind` without adding
platform types to the contract package.

`IExternalResourceResolver.ResolveAsync` returns endpoints plus the provider manifest hash and
export schema version when known. The gateway controller owns policy: compatible resolutions are
`Running`; optional unresolved declarations are `Skipped`; required unresolved declarations stay
nonterminal until the per-resource readiness budget fails the startup gate; and a missing
referenced endpoint is `Failed`. A changed manifest hash with all referenced endpoints still
present is reported as `ManifestDrift` while remaining `Running`. Stop and delete detach only the
consumer's observed external state; they do not operate on the provider application.

There are two portable documents with different jobs:

- `ApplicationModelDocument` (`cohesion/model/v1`) is the immutable graph: invocation intent,
  dependencies, manifests, platform-neutral plans, and external declaration/closure/realization/
  binding metadata. Describe mode writes this document without contacting a platform, and an
  import retains static, file, and gateway `RemoteReference` bindings. An arbitrary resolver
  supplied through `Bind(...)` is deliberately emitted as unbound because executable code is not
  portable; the importing platform must contribute its binding again.
- `ApplicationExportDocument` (`schemaVersion: 1`) is public discovery: application/environment
  version, optional public JWK, resource manifest hashes, observed internal/public endpoints, and
  the complete model document. It has source-generated `Create`/`Parse`/`Load`/`Save`/`ToModel`
  paths and validates cross-document identity, hashes, kinds, and endpoint names.

`export.json` remains a storage convention rather than a side effect of this Layer-1 package.
`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane` implements item 23a's authenticated
HTTP publication and fetch endpoints over this exact document. Kubernetes export/import remains
a platform integration and is not implemented here.

## Application-set composition

`Application.CreateSet(gateway, args)` requires an `IMultiModelApplicationGateway` and creates an
`IApplicationSet`. Each generated or hand-written `ApplicationDeclaration` pairs an application
identity with an `IApplicationModelResolver`. The supplied resolvers cover:

- `Executable(path)` — invoke the member gateway with `--mode describe`;
- `File(path)` — reconstruct the model embedded in an application export;
- `Gateway(address, client)` — obtain that export through `IControlPlaneClient`;
- `ControlPlane(executablePath, exportPath)` — use executable describe in Development and the
  exported model otherwise.

Member models are resolved at `RunAsync` start in declaration order. The set rejects duplicate
declarations and a resolver returning the wrong application identity, applies invocation-level
external overrides to each imported model, and binds an external targeting a sibling member through
`IApplicationSetExternalResourceResolver`. That direct observed-state lookup runs before the
external's configured control-plane/file/static resolver and falls back only when the sibling is
not observable. The set dispatches `Run`, `Apply`, or `Teardown` through the shared gateway;
`Describe` writes a JSON array of every member model without gateway contact; `Render` validates
the ordered collection and dispatches it through `IApplicationGatewayRenderer`; and `Bootstrap`
does the same through `IApplicationGatewayBootstrapper`. A single application dispatches those
optional capabilities with a one-model collection. An unsupported capability names the selected
gateway before any application-set gateway validation. The base
multi-model contract preserves model
order and requires state to be scoped by `(application, resource)`; equal resource identifiers in
different applications must not collide. SDK generation of `Applications.<Name>` is a consumer
convenience over this seam and is not required by the contract itself.

In Development, executable resolution applies `--realize` only after the first description shows
that the member declares the requested external. An imported export must already record a matching
external as realized, and the set rejects any requested name that no member realized.

## Lifecycle and error model

- `Application.CreateBuilder(ApplicationName, args)` → fluent
  `AddResource(...).DependsOn(...)` + `UseGateway(...)` → `Build()`. `UseName`
  remains available for callers that start from the parameterless overload. Invocation intent
  carried by the immutable model includes `--adopt` ownership consent and
  `--restart-orphans` local-process recovery policy; platform gateways decide how to realize it.
- `--realize <external>` is validated during `Build()`. It is accepted only in Development for
  gateway identities `local`, `inprocess`, and `docker`, requires an embedded target manifest,
  and replaces the requested external plus its reachable same-application closure with ordinary
  planned resources. Cross-application references discovered within that closure remain external,
  and realized members retain their original application identity for runtime naming.
- After run cancellation, `CohesionApplication` lets the selected gateway apply its own
  per-resource stop budgets. It does not impose one 30-second outer timeout across a
  reverse-ordered resource set, which would truncate later resources' declared grace periods.
  Cancellation that arrives while gateway startup is still blocked is handled identically: the
  partially started session is stopped without uninstalling persistent resources, and Run
  completes after that stop rather than surfacing lifetime cancellation as startup failure.
- `Build()` validates: unique resource names (enforced eagerly on `AddResource`),
  at least one realized resource, all explicit dependencies and required manifest references
  present, no dependency cycles
  (DFS), a selected gateway, an RFC 1123 application name, each typed override, and
  every computed plan, then asks the selected gateway to validate realizability. Planning
  deliberately happens here rather than in MSBuild or
  when the resource is added.
  Every failure is an `InvalidOperationException` with an actionable message; there
  are no custom exception types in this library (an area-scoped root can be added
  later if the surface grows).
- In Run mode, `IApplication.RunAsync` mirrors `Host<TContext>.RunAsync`: a linked
  `CancellationTokenSource` plus a `TaskCompletionSource` completed on cancellation.
  It `StartAsync`es the gateway, awaits cancellation, then `StopAsync`es supervision
  using the gateway's own resource-aware stop bounds. Stop retains persistent platform objects;
  Apply performs a reconcile pass and Teardown dispatches `UninstallAsync(model)`. Describe emits the model document
  (or the declaration-ordered document array for an application set) and never contacts the selected
  gateway. Render and Bootstrap use optional gateway capabilities, receive the original cancellation
  token, write to standard output, and remain platform-contact-free.

## AOT posture

This package is `Core`-only and AOT-clean: capability matching is `is`-based and
there is **no reflection-based serialization**. Manifest, plan, and describe-mode
model documents use explicit `JsonSerializerContext` contracts. Entry-assembly name
fallback is used only in Development and is slugged before validation; assembly
attributes are never read at run time. The generated value types use
`System.Text.Json` converters that are source-emitted, not reflection-based.

**Note on the family:** the sibling `…Gateway` base is also AOT-gated. Platform packages own
their own AOT posture; this package neither references nor asserts an implementation status for
Docker or Kubernetes integrations.

## Family relationships

- `…ApplicationModel.Gateway` (Layer 2a) — the guided `ApplicationGateway` base +
  `LocalGateway`; implements the control-plane contracts defined here.
- `…ApplicationModel.Gateway.ControlPlane` — the hosting-free authenticated HTTP server/client
  behind `Gateway(...)`, plus the resource command dispatch seam.
- `…ApplicationModel.Gateway.{Platform}` (Layer 2b) — platform compilers and controllers supplied
  outside this contract package.
- `{Resource}.ApplicationModel` (Layer 3d) — Core-only manifest packages that
  provide a typed `PlannedResource`, `Add{Resource}(manifest, options)`, the area's
  planner when it differs from `GenericPlanner`, and the resource-side default
  control-plane contract served by `{Resource}.Hosting`.

## Non-goals

- Hosting a process (DI/Config/Logging composition stays in `{Resource}.Hosting`).
- Building container images (delegated to the SDK container tooling, upstream, per
  resource).
- Referencing or rebuilding any `{Resource}.Hosting` runtime — an out-of-process orchestrator
  only needs contracts, manifests/plans, and deployable artifacts.
- Platform object formats, Kubernetes types, and process supervision — those live
  in gateway/compiler packages. The platform-neutral resource manifest, realization
  plan, and describe-mode model document are contracts of this package.

## Declarative resource commands (T7a)

`IResourceCommand` records desired state owned by the declaring application. Its target is
an exact resource instance in that application's graph, including external nodes.
`ResourceCommands.Create` requires source-generated `JsonTypeInfo<T>` metadata, snapshots
UTF-8 JSON, sorts object properties ordinally at every nesting level, and derives a lowercase
SHA-256 id from length-prefixed command kind, target application/resource identity, and canonical
payload bytes. Changing a payload changes the id; `Owner`, the nonblank provider conflict `Key`,
and `Optional` remain separate ownership and gating facts. The typed area verbs include their
logical key fields in the payload. Payloads are copied when created and again when the model is built.

`IResourceCommandDescriptor` is an additive authoring seam. Area-owned descriptor interfaces
extend it and internal wrappers delegate command accumulation to the original descriptor.
`IApplicationBuilder.AddCommand` also accepts an explicit declaration. `Build()` checks the
owner against the declaring model, exact target graph membership, accepted manifest command
kinds, nonblank keys, duplicate ids, and conflicting declarations for the same target/key,
regardless of command kind. One desired graph declares one operation per provider ownership
key; a subsequent model can replace or withdraw that operation. The model and each built command descriptor expose
read-only command snapshots; built descriptors reject subsequent mutation.

Dependency normalization accepts an older wrapper that exposes the exact registered `Resource`
instance. This preserves unchanged SecretStore-style wrappers without requiring them to implement
a new interface. Matching names or value equality never establish graph membership. New typed
wrappers preserve the same invariant, including when a remote reference is rebound.

`ApplicationModelDocument.Commands` retains declarations through describe/export/application-set
roundtrips, validates canonical bytes and deterministic ids on import, and maps each imported
target back to that imported model's resource instance. Old documents without commands import an
empty list. These portable payloads are desired state and must never contain secret material;
this slice supplies database names/principal names and nonsecret configuration values only.
Observed command status is a separate resource state-manager contract.

Command execution belongs to the gateway after Running and before dependent reconciliation.
Area default-control-plane handlers retain ownership and reject unsupported mutation capabilities.
The contract package adds no client, hosting, DI, platform, or runtime serialization dependency.
`IControlPlaneExternalResourceResolver` exposes a peer gateway address without exposing the
internal resolver implementation. In application sets the direct sibling resolver preserves its
fallback's peer address, so generic command delivery can select a peer only when no sibling target
is available. Static and file bindings expose no control-plane address. Exports validate observed
command target membership, required identity fields, defined statuses and duplicate target/owner/id
tuples. A rejected unsupported command kind is valid audit data even without a desired declaration.

## Trust grant command options

GatewayCommand carries an immutable AllowedCommandKinds list. Repeatable, comma-separated
--allow options are valid only in trust-add mode. Absent or empty grants mean unrestricted kinds;
trust-issue rejects the option. ApplicationModel parses and carries policy; the serving gateway
enforces it on apply and delete. The CLI's --against option remains deferred.
