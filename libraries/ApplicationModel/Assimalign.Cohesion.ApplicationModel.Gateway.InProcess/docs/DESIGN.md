# Assimalign.Cohesion.ApplicationModel.Gateway.InProcess Design

## Design intent

The in-process gateway is Cohesion's sanctioned composition root. It changes the realization unit
from one operating-system process per resource to one adopted Cohesion host per resource inside a
single process; it does not introduce a second application model or a resource-area hosting API.
The immutable `ResourcePlan`, controller selection, dependency order, initial readiness gate,
observed state, reconcile semantics, and reverse-order stop remain owned by
`ApplicationGateway`.

The package implements the `inprocess` platform only. It references
`Assimalign.Cohesion.ApplicationModel.Gateway`, plain `Assimalign.Cohesion.Hosting`, and
`Assimalign.Cohesion.Hosting.Resources`; it never references an `<Area>.Hosting` package. The
consumer's `CohesionGatewayInProcess=true` project references bring the selected member runtimes
into the Composite executable.

## Admission and colocation boundary

In-process realization is available only for a real project reference whose manifest reports
`artifact.composable=true`. An enabled resource's generated module initializer registers its
compiler-rooted assembly entry point and a factory for its area default control plane. A disabled
executable has neither registration nor manifest and is never nested. A manifest-package
reference has no statically linked entry point and remains image-only. A non-composable kind
declares that its data plane cannot share this crash domain.

Validation refuses those shapes before artifact gathering or target contact and names both the
resource and the selected gateway. The controller also rejects plan features outside the
in-process subset, including an unsupported schema, non-self artifact, replica count other than
one, or an unsupported probe kind. An ordinary public exposure is admitted only when it maps
exactly to one supported loopback TCP endpoint service. The exposure remains metadata for an
outer gateway to inherit and realize; it does not create a separate platform object inside the
Composite. Malformed exposure mappings and unsupported transports are refused with the member
resource named. Admission is a build-time fact; the runtime does not scan or dynamically load
assemblies to discover candidates.

The entry binding itself is registered twice by generated code, in two registries with one
lookup. The generated `Add<Name>()` verb binds the descriptor it returns
(`InProcessResourceDescriptorExtensions.InProcess`, keyed by the built resource instance), and the
generated `Gateway.CreateBuilder(args)` binds every enabled, composable project manifest
(`InProcessResourceManifestExtensions.InProcess`, keyed by the manifest's application and resource
names). The planned resource snapshots its manifest, so the manifest registry cannot key on the
instance; identity is the only stable key. The second registry is what makes a resource added
through the area verb (`builder.AddWeb(Manifests.DocsWeb)`) or a third-party application model's
verb over the same manifest (`builder.AddViuWeb(Manifests.DocsWeb)`) colocatable: the generated
verb is one caller of the binding, not its owner. Both registrations carry the same
`DynamicDependency` root, so trimming keeps the entry point whichever path the apphost uses.
Rebinding either key to a different assembly or content root is refused.

## Entry invocation and host adoption

`ProcessHost` uses a start-on-add model. For each resource, in topological order, the
`InProcessPlanController` compiles a pure desired description, prepares its invocation context,
and asks `ResourceRuntime.InvokeEntry` to invoke the registered `Assembly.EntryPoint` on its own
thread. The generated entry-point root is the single sanctioned reflection boundary and is kept
by generated trimming metadata. No assembly scanning, runtime code generation, synthesized
`Program` type name, or alternate `CreateHost` convention is permitted.

The member's normal area builder calls `ResourceRuntime.HostBuilt`, which records the exact
`IHost`. Its installed runner releases that host to `ProcessHost` only after the entry point calls
`RunAsync` and lifecycle ownership has been claimed, so synchronous post-build route and pipeline
composition finishes before adoption. An entry point that intentionally returns after building an
idle host surrenders it only after successful completion; `ProcessHost` then owns its direct
start/stop lifetime through `AsService()`. The per-invocation default control plane remains attached
to and served by that member host. Host-start failures and early entry-point completion surface
through the resource state manager and abort the same initial gate used by every gateway.

Members are registered for reverse-order stop only after adoption. They are never placed in a
pre-populated outer `HostedServices` collection, because starting such a collection would bypass
dependency ordering and readiness admission.

Failed or cancelled adoption never waits indefinitely for a cancellation-ignoring member. Cleanup
continues as a background observation and disposes an eventually surrendered host. Once a member
is adopted, removal attempts graceful stop, entry completion, disposal, and ownership release
independently; one failed phase cannot skip the phases behind it or retain a stale generation.

## Ambient isolation

Each invocation receives its own `ResourceContext` through `ResourceRuntime.CreateScope`. The
context carries the member's application and resource names, environment, `inprocess` gateway
identity, absolute content root, loopback endpoint bindings, mounts, settings, observed
references, application trust key, bootstrap credential, and other frozen `COHESION_*` contract
values. `AsyncLocal` propagation isolates parallel hosts; process environment variables are not
mutated to emulate per-resource state.

Members retain independent service providers, configuration, logging, health contributors, and
default control planes. Registration is keyed by assembly, while invocation state is scoped to
one ambient frame. No member's control plane or health contributors can leak into another
member's builder. The process-wide console writers are installed once behind a reference-counted
router. They buffer each ambient context independently and emit complete lines with a
`[<resource>]` prefix, so concurrently written member output remains attributable without
changing a member's logging configuration.

## Endpoints, references, and Composite remapping

Declared endpoints receive persisted loopback bindings. The assigned address is published through
`IApplicationResourceStateManager.SetState(..., observedEndpoints)` with the first `Running`
transition and remains stable across an in-process restart. Listeners bind during member host
startup and release during stop, so the replacement host can bind the same address.

A Composite manifest flattens each member endpoint as `<member>-<endpoint>`. The outer gateway
therefore exposes dependency variables shaped as
`COHESION_DEPENDENCY_<COMPOSITE>_<MEMBER>_<ENDPOINT>_*`. Inside the Composite, `ProcessHost`
projects the member's view into typed `ResourceContext` dictionaries: its own flattened endpoint
becomes `<endpoint>`, and each observed dependency becomes a `<resource>:<endpoint>` reference.
Required dependency endpoints remain available after initial admission, including while the
dependency is `Degraded`. Optional dependency endpoints are supplied only while the dependency is
`Running` or `Degraded`; a missing optional dependency stays absent.

All addresses remain loopback TCP in the first version. In-memory transports would require a
client-factory seam in every resource area and are not part of this package.

## Mount remapping and content roots

Configuration and secret inputs use in-memory `ResourceMount` values. Volume mounts use private
directories, while a nested Composite reuses its corresponding outer claim path. A Composite manifest
flattens a member mount as `<member>-<mount>`; the outer carrier named
`COHESION_MOUNT_<COMPOSITE>_<MEMBER>_<MOUNT>_PATH` is projected inward as the member's ordinary
`<mount>` entry in `ResourceContext.Mounts`. Member code continues to use the same generated
`Resource.Mounts` accessor in every topology.

Every member receives its own absolute content root. File lookup must never fall back to the
Composite's working directory merely because all hosts share a process.

## Readiness, liveness, and health

Entry-point host surrender and child-host adoption must complete within the configured readiness
budget. After the member host reaches `Started`, startup and readiness run in that order using the
plan's declared probes. When a role has no explicit probe, the gateway probes the member's
registered default control plane over its loopback endpoint. Only those default control-plane
requests carry the member bootstrap bearer; explicit application probes never receive it. HTTP and
TCP probes retain the common gateway behavior, HTTP requires exactly 200, and 404/405 fail the
initial gate immediately. Exec and gRPC probes are refused because they cannot be isolated or
honored by the local in-process compiler.

The base gateway waits on `plan.Workload.Gate.Terminals` and admits the member only when the
reached state occurs in `Gate.Satisfying`. `Degraded` is observational and never revokes a
dependent's initial admission.

The package publishes every member's health into the gateway resource-state view; there is no
process-wide health-contributor collection. Serving the Composite's exported model and aggregate
health from that view belongs to `Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane`
(design item 23a), not to this platform package.

## Offline render

`--mode render` uses the base gateway's `cohesion/local-plan-set/v1` document with
`kind: inProcessHost`. It folds each `ResourcePlan` into an ambient host unit, carries the generated
binding's declared assembly identity and content root, and leaves endpoint allocation and mount
handles visibly deferred. The renderer never invokes the entry point, creates the state directory,
allocates a port, resolves a mount or credential, or starts `ProcessHost`. Content-root existence is
therefore checked during artifact gathering, not during validation or rendering.

## Restart and shutdown

A failing liveness check moves a running member to `Degraded`. After three consecutive failures,
the supervisor applies exponential backoff from one to thirty seconds and stops after five restart
attempts by default. A
restart transitions `Degraded -> Stopping -> Starting -> Running`, requests graceful host stop,
awaits completion within the member's stop grace, disposes the adopted host, and invokes the same
registered entry point again under a fresh scope carrying the same context values and ports.

Restart never re-admits dependencies. A successful later liveness check may restore `Running`
before the threshold is reached. In-process invocation preserves `cohesion/sysexits/v1` through
`ResourceEntryExitException`: 64 and 70 are final even under `Always`; 69 and 75 restart under
`OnFailure` or `Always`; and a clean exit restarts only under `Always`. The same bounded loop covers
initial and later pre-readiness failures, so repeated restartable startup exits continue until the
member reaches `Running` or exhausts the configured limit. Expiry of a post-adoption readiness gate
is treated as restartable code 75; an unclassified pre-readiness failure is 70 and final.

Gateway stop cancels supervision and stops adopted hosts in reverse dependency order. Runtime
stop retains persistent endpoint and volume state; destructive uninstall removes the exact
validated per-resource state tree, including volume claims, as well as its port allocation.

## NativeAOT and trimming

T7a command delivery uses the exact adopted host's default control plane. `ProcessHostContext`
matches the member's application/resource identity and asks `ResourceRuntime.TryGetControlPlane`
for the weak host registration. The generic gateway applies commands only after Running, before
dependents, and removes owned declarations before target teardown. The lookup neither creates a
second control plane nor changes generated consumer registration.

The package is NativeAOT-compatible. Member entry points are rooted by generated metadata and
bound statically from project references. The only reflective operation is invocation of the
already-rooted `Assembly.EntryPoint` through `ResourceRuntime`. The implementation performs no
assembly scanning, dynamic loading, runtime compilation, expression compilation, or
`Microsoft.Extensions.*` integration.

The closing validation is a published Composite containing enabled Web and Database members. It
must pass ordinary build/tests plus trimmed NativeAOT publication and container publication, and
must prove per-member context isolation, endpoint and mount remapping, readiness ordering,
liveness restart, reverse stop, and entry-point preservation.

## Non-goals

- Providing isolation comparable to separate processes or rolling updates inside one process.
- Nesting disabled executables, manifest-package references, images, or non-composable kinds.
- Making resource-area builders orchestration-aware.
- Introducing a second host lifecycle or generated host-construction entry point.
- Replacing loopback transport with an in-memory connection fabric.

HTTPS mounts retain the existing FromBytes handover. The context constructor receives endpoint certificate mappings from PortBinding and the protected trust-bundle path is copied into its ambient values. HTTP probes use the context's outbound trust validator, preserving hostname validation and rejecting unrelated roots.

## Telemetry invocation values (31b)

InProcessPlanController.Compile copies the plan environment and applies the gateway's public IResourceTelemetry view through ApplyEnvironment. InProcessContextFactory then materializes `.state/telemetry.headers` through ILocalResourceState.MaterializeRuntimeFilesAsync immediately after the trust bundle, including empty-content removal. CreateAmbientValues carries endpoint, protocol and that protected path into ResourceContext; Hosting.Telemetry reads them through the additive TryGetEnvironmentValue member, with no process-environment fallback. The headers use the same ResourceMount.ReadAllBytes path as local hosts. Member stop invokes the registered telemetry IHostService for bounded final-batch flush. Discovery ordering, scoped emitter credentials and the absence of an inferred dependency follow the gateway design.

## Gateway boundary (Phase 19)

The controller reads telemetry with `IResourceControlContext.GetTelemetry()` and carries only
`IResourceTelemetry` into its compilation. Context preparation uses `ILocalResourceState`
for endpoint allocation and protected runtime files; the gateway package owns persistence and
file protection. Offline rendering calls the inherited `RenderLocalPlanSetAsync` operation
with an artifact identity/content-root tuple. No concrete gateway internals cross this boundary.
