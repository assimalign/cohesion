# Resource control plane redesign

Status: **APPROVED — implemented outside Database; Database source remains frozen for separate branch migration, 2026-10-24.** No compatibility or Database build workaround is retained. See the [current implementation contract](../libraries/ApplicationModel/RESOURCE_MANAGEMENT.md) and [validation record](resource-control-plane/VALIDATION.md).

- [Implemented contracts](resource-control-plane/CONTRACTS.md)
- [CORS and Kubernetes walkthrough](resource-control-plane/CORS_WALKTHROUGH.md)

## Boundary to review

[Likely] The manifest is the integration boundary. Hosting.Resources describes the runtime state
a resource exposes. Application Model consolidates resource documents and dependency descriptors.
The gateway interprets the runtime contract, deploys infrastructure, resolves live addresses and
coordinates management changes across that infrastructure.

Application Model has **no reference to Hosting.Resources, resource implementations, or a new
shared Resources package**. Its public contracts contain no state handlers, mutation types,
instance inventories, desired-state stores or management coordinators. New gateway contracts
belong in the gateway family. The previous shared-foundation-package proposal is removed.

The [implementation contract](../libraries/ApplicationModel/RESOURCE_MANAGEMENT.md) shows the current
assembly dependency diagram. Manifest exchange does not imply an assembly reference. Health
contracts now live in Hosting.Resources. The gateway requires no reference to area runtime
assemblies. A separately opted-in in-process host may still
load resource executables; manifest composition never requires that mode.

| Owner | Responsibility | Output |
| --- | --- | --- |
| Resource through Hosting.Resources | Advertise supported state; validate, read and apply it locally | Runtime manifest and local control plane |
| Application Model | Import documents; assign application identities; consolidate deployment requirements, bindings and dependencies | Consolidated application manifest |
| Gateway family | Interpret contracts; deploy infrastructure; observe hosts; maintain desired state and distribute changes | Deployment observations, management API and operation status |

Feature libraries continue to own business behavior. Hosting adapters bridge that behavior to
Hosting.Resources: Web.Cors owns policy validation/evaluation; the optional Web.Hosting.Management
package owns the CORS management adapter. Web.Cors does not acquire a Hosting.Resources dependency.

## Three distinct documents

| Document | Producer | Contents | Reader |
| --- | --- | --- | --- |
| Resource runtime manifest | Hosting.Resources at resource startup | Logical management binding; discovered state names, schemas and delivery semantics; effective configured baselines | Gateway protocol adapter |
| Consolidated application manifest | Application Model | Resource artifacts/deployment requirements; dependency graph; opaque producer management bindings | Selected gateway |
| Deployment observation | Gateway/platform observation path | Deployment incarnation; actual consumer addresses; current instances and management addresses; progress | Management API and diagnostics |

The runtime manifest describes **what is manageable**, not a dump of current values. Current CORS
values come from the local control plane. Pod IPs, credentials and operation receipts are gateway
runtime data. They do not get written back into the portable application manifest.

The application's resource entries retain a generic deployment envelope and an opaque `runtime`
JSON member. Application Model validates its own envelope and graph, preserves the runtime document
and its integrity digest, and does not interpret CORS, state schemas or delivery semantics. The
gateway checks runtime schema versions and live capability compatibility before accepting deployment
or management. Unknown required protocols fail explicitly at the gateway.

This remains a versioned **wire contract** between producer and gateway. Hosting.Resources owns its
runtime schema/exporter; gateway protocol adapters own their wire DTOs/serializers. Application
Model owns its generic envelope. No shared runtime contract assembly connects these layers.

## Generation and composition

[Certain] The SDK emits `resource.json` through `CohesionCreateResourceManifest`. New non-Database
resources use `cohesion/resource/v2`, without commands, static state definitions or initial values.
Its opaque runtime fragment contains only the management binding. The old resource/v1 manifest
path is retired; frozen Database sources may fail to build until separately migrated.

Implemented flow after the 2026-10-24 refinement:

1. SDK packaging combines the management binding with artifact, endpoint, probe, mount and setting
   declarations, without starting the executable or inferring feature configuration.
2. ApplicationModel imports resource documents and composes identities, logical resource groups,
   generic bindings and dependency edges. Group membership does not define infrastructure ownership.
3. Startup handlers own their schemas, canonical digests and effective configured baselines.
   Hosting.Resources freezes this catalog before attaching the host. The obsolete MSBuild state
   and initial-state items and generated registration slots are removed.
4. The selected gateway deploys infrastructure and discovers the authenticated runtime catalog
   from starting instances before readiness. Kubernetes object types and current hosts stay in
   the gateway layer; runtime capability discovery does not modify the portable application model.

Deployment metadata is available without evaluating callbacks or loading resource assemblies.
Feature configuration is a startup concern: adapters capture configuration after appsettings and
environment precedence have been applied. Later remote overrides leave that baseline intact.
Secrets remain provider references, not embedded values. General appsettings binding for every
area builder API is a separate planned change.

Application Model composes the dependency without learning about CORS:

```text
AddResource("database", importedDatabaseDocument)
AddResource("api", importedApiDocument)
DependsOn("api", "database")
Build() -> consolidated application manifest
```

There is no model-level `WithCors`, resource handler or Kubernetes inventory. Optional area authoring
packages may supply deployment descriptors, but generic manifest import never requires area runtimes.

## Runtime management

The resource-side `IResourceControlPlane` exposes its manifest and validate/read/apply dispatch.
It owns no command history, cluster inventory, orchestration policy or distribution algorithm.
Feature adapters retain actual state and the current applied revision atomically, so stale writes
can be rejected. Current applied state is distinct from a past-request ledger.

The gateway owns the logical operation:

1. Authenticate and authorize the caller for a resource and state slot.
2. Interpret the runtime document, validate the payload/schema and check live compatibility.
3. Atomically persist desired state, a monotonic revision and operation receipt with a conditional
   write. Return `202 Accepted` only after persistence succeeds.
4. Discover instances through the platform adapter and deliver directly through verified management
   transports. Reconcile failures and newly observed processes to the latest revision.
5. Report desired state and per-instance observations separately. One successful reply does not
   establish resource-wide completion.

| Manifest semantic | Example | Gateway behavior |
| --- | --- | --- |
| `everyInstance` | Replace CORS configuration | Reconcile every current and future process |
| `sharedState` | Ensure a database exists in shared storage | Coordinate one capable instance at a time; require backend idempotency/fencing |

The producer fixes that semantic. Callers cannot broadcast shared storage mutations or select one
pod for logical CORS state. Non-idempotent actions need a separate action contract. Request IDs alone
do not establish exactly-once side effects.

Gateway persistence owns authorized management overrides relative to declared initial state.
Reapplying the application manifest preserves overrides. Clearing one reveals the current declared
baseline; a missing baseline does not imply a destructive reset. Reset/absence semantics must be
declared before accepting them. Application Model preserves these declarations as data and does not
enforce management policy.

## Kubernetes and ten API instances

Kubernetes orchestration creates workloads, Services, bindings and mounts from the consolidated
deployment requirements. Observation supplies ordinary Service/Ingress endpoints and individually
addressable management instances. Management delivery uses the latter.

Instance identity includes platform identity **and process incarnation**. A container restart can
create a fresh process on the same platform host; a previous acknowledgement cannot represent it.
Inventory includes starting/not-ready instances to allow bootstrap before serving traffic. Management
channels verify resource/process binding and trust. They are not arbitrary caller-supplied addresses.

A CORS update is accepted once and reconciled to ten instances. Nine successes plus one failure is
degraded progress. When HPA adds an eleventh instance, the same desired revision is applied before
that process is admitted to traffic. Desired replica count comes from the live scale target; state
reconciliation preserves HPA ownership of scaling fields. Convergence is dated against a complete
inventory and checked again on membership changes.

Continuous reconciliation needs a surviving gateway/controller or equivalent platform mechanism.
A deployment command that exits cannot itself deliver future updates to HPA instances. A platform
must supply ongoing execution before advertising that guarantee. The implementation uses a running gateway reconciliation loop, protected bootstrap projections and readiness gating. Live-cluster validation remains separate from hermetic tests.

Instance state and applied revision/digest commit atomically. Older revisions are rejected; equal
revision/digest is already current; equal revision with different content conflicts. Gateway recovery
re-observes processes and resumes latest intent. Shared storage additionally enforces a backend
fence. This provides eventual convergence; simultaneous fleet cutover requires another design.

## Management surface

```http
GET /cohesion/v2/resources/{resource}/state-contracts
GET /cohesion/v2/resources/{resource}/state/{name}/{key}
PUT /cohesion/v2/resources/{resource}/state/{name}/{key}
GET /cohesion/v2/resources/{resource}/operations/{operationId}
GET /cohesion/v2/resources/{resource}/instances
```

Writes require a conditional revision and stable client request ID. Bodies contain state, never
hosts. Reads show effective desired state and convergence; individual observations require diagnostic
authorization. Gateway capability/read/write grants must be explicit. Existing command grants must
not silently acquire broader management access.

## Approved refactor scope

| Area | Change |
| --- | --- |
| Hosting.Resources | Runtime manifest/export; immutable handler registry; local dispatch |
| Resource hosting adapters | Explicit state declarations and feature-to-control-plane bridge |
| SDK/build integration | Matching metadata/registration generation; resource packaging |
| Application Model | Generic manifest preservation/composition and dependency descriptors; retain Core-only boundary |
| Gateway family | Protocol interpretation, management coordination, persistence and live deployment observations |
| Platform gateways | Deployment, inventory, direct delivery, continuous reconciliation and admission |
| Existing commands | Classify desired state versus actions; choose compatibility migration explicitly |

`ResourceMount` remains an input/storage abstraction. Health and stop remain host lifecycle concerns.
The state protocol is independent of explicit programmatic command delivery. Producer command
catalogs and the resource/v1 manifest are retired. Database source remains untouched for separate
branch integration, without a compatibility shim or build allowance.

## Review validation

The probe compiles Hosting.Resources notation, Application Model notation and gateway notation as
separate assemblies. The model probe has no project references; gateway references the model only.
This checks CLR ownership and signatures, not behavior or NativeAOT publishing.

Implementation acceptance must cover catalog/handler consistency, manifest preservation, unknown
protocols, deployment observation, ten-instance partial failure, HPA growth, process restart,
gateway recovery, revision races and mixed-version deployments.
