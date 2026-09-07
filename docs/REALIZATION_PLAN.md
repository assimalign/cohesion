# Cohesion Realization Plan Contract

`ResourcePlan` is the platform-neutral intermediate representation between a resource-area
planner and a platform compiler. The normative schema identifier is
`cohesion/plan/v1`.

The resource manifest remains a facts-only build artifact. A gateway application computes one
plan per realized resource at `IApplicationBuilder.Build()` from the immutable manifest, typed
deployer options, the selected environment, and referenced manifests. The resource-area planner
owns portable realization decisions; `GenericPlanner` supplies the inherited default. After
`ResourcePlanValidator` accepts the result, exactly one compiler for the selected platform turns
the plan into platform objects. A compiler never selects behavior by resource-area assembly,
resource kind, or CLR type.

The plan is not delivered to the resource process and does not contain platform objects, resolved
credentials, Kubernetes storage classes, Docker options, or other platform-specific settings.

## Wire format and compatibility

Plans are UTF-8 JSON. Property names are camel case, enums are serialized by name, and
`ResourceName` and `ArtifactRef` are JSON strings. The source-generated
`ResourcePlanJsonContext` is the .NET definition of the wire shape.

Every named property outside the contents of `hints` is specification. A consumer must reject:

- an unknown specification field;
- an unknown enum value;
- a missing required field or an invalid null;
- a schema identifier it does not support.

The rejection happens during `Build()` or `--mode render`, before artifact gathering or platform
contact. A newer plan must never be partially interpreted as an older schema.

Entries inside `hints` are advisory. A compiler that does not recognize a hint must warn once for
each distinct unknown hint key during one build or render operation, ignore that hint, and
continue. It must not silently reinterpret an unknown hint as specification. A recognized hint
may refine a compiler choice only where the specification leaves that choice open; it cannot
weaken or contradict a specification field.

The schema identifier must be bumped when any specification field or enum member is added,
removed, renamed, changes type or cardinality, or changes meaning. This includes adding an
otherwise optional specification field. Adding, removing, or changing a hint key never bumps the
schema because hint discovery is explicitly warning-and-ignore compatible.

Planner output order is deterministic: manifest-derived collections follow declaration order,
the synthesized governing service follows endpoint services, and readiness gates use the order
shown below. Compilers must not use order to invent dependencies or other semantics not
represented by the plan.

## Contract records

### `ResourcePlan`

| JSON field | Type | Contract |
|---|---|---|
| `schema` | string | Must equal `cohesion/plan/v1`. |
| `resource` | string | The manifest resource name. It must equal `ResourceManifest.Name`. |
| `kind` | string | The facts-only manifest kind. It must equal `ResourceManifest.Kind`; a compiler does not dispatch on it. |
| `workload` | `WorkloadSpec` | Required controller, replica, gate, and stop semantics. |
| `container` | `ContainerSpec` | Required portable execution unit. Version 1 has exactly one container specification per resource. |
| `volumes` | array of `VolumeSpec` | Materialized storage claims. Empty when the resource has no `Volume` mount. |
| `services` | array of `ServiceSpec` | Stable logical addresses, including a governing service when stable identity requires one. |
| `exposures` | array of `ExposureSpec` | Public exposure requests. Private endpoints never appear here. |
| `hints` | object of string values | Advisory compiler hints. Unknown keys warn once and are ignored. `GenericPlanner` emits an empty object. |

### `WorkloadSpec`

| JSON field | Type | Contract |
|---|---|---|
| `kind` | `WorkloadKind` | Carried verbatim from `ResourceManifest.Lifecycle.Workload`. |
| `replicas` | integer | Positive requested replica count after a typed override; it cannot exceed manifest `maxReplicas`. |
| `stableIdentity` | boolean | `true` exactly for `StatefulSet` in version 1. |
| `gate` | `ReadinessGate` | Initial dependency-admission rule defined by O30. |
| `stopGraceSeconds` | integer | Positive graceful-stop budget, carried from the manifest. The generic default is 30 seconds. |

`WorkloadKind` has four version 1 values:

- `Deployment` — continuously running replicated workload without stable replica identity;
- `StatefulSet` — continuously running replicated workload with stable identity and per-replica
  claims;
- `DaemonSet` — continuously running workload with one instance per eligible node on platforms
  that have nodes;
- `Job` — finite workload whose clean stop satisfies readiness.

### `ReadinessGate`

| JSON field | Type | Contract |
|---|---|---|
| `terminals` | array of `ResourceLifecycle` | Non-empty, duplicate-free states that complete the initial readiness wait. |
| `satisfying` | array of `ResourceLifecycle` | Non-empty, duplicate-free subset of `terminals` that counts as success. |

Only the following version 1 gates are valid:

| Workload kind | `terminals` | `satisfying` |
|---|---|---|
| `Deployment`, `StatefulSet`, `DaemonSet` | `Running`, `Failed`, `Stopped` | `Running` |
| `Job` | `Stopped`, `Failed` | `Stopped` |

This is the O30 gate rule. The base gateway waits for any state in `terminals` and admits a
dependent only when the reached state is in `satisfying`. Consequently `Stopped` is a fast
failure for every long-running workload, while a cleanly stopped `Job` succeeds. `Failed` never
satisfies a gate. `Degraded` is observable after readiness but is not gating and never re-gates a
dependent already admitted by `Running`.

`ResourceLifecycle` is serialized by name. Its version 1 members are `Unknown`, `Pending`,
`Building`, `Provisioning`, `Starting`, `Running`, `Degraded`, `Stopping`, `Stopped`, `Failed`,
`Blocked`, and `Skipped`; the validator permits only the exact gate sets above in a plan.

The plan contract and `GenericPlanner` define the `Job` shape now. Until orchestration item 26
lands the current `ResourcePlanValidator` deliberately rejects a `Job` during application
`Build()`, so a gateway cannot realize one prematurely.

### `ContainerSpec`

| JSON field | Type | Contract |
|---|---|---|
| `name` | string | Must equal the resource name. |
| `artifact` | `ArtifactRef` | Must be the string `self`; it refers to the artifact produced for this manifest. No other artifact reference exists in version 1. |
| `ports` | array of `PortBinding` | Exactly one binding for every manifest endpoint. |
| `mounts` | array of `MountBinding` | Exactly one binding for every manifest mount. |
| `environment` | object of string values | Manifest environment plus the gateway-owned `COHESION_APPLICATION`, `COHESION_RESOURCE`, and `COHESION_ENVIRONMENT` identity values. |
| `probes` | array of `ProbeMapping` | One-to-one mappings for the probe roles explicitly present in the manifest. |

`ArtifactRef` is a JSON string. The only valid version 1 value is `self`.

### `PortBinding`

| JSON field | Type | Contract |
|---|---|---|
| `endpoint` | string | Name of the bound manifest endpoint. |
| `containerPort` | integer | The endpoint's declared container port. |
| `protocol` | string | The declared transport protocol, normally `tcp` or `udp`. |

### `MountBinding`

| JSON field | Type | Contract |
|---|---|---|
| `mount` | string | Name of the bound manifest mount. |
| `containerPath` | string | Path visible to the resource execution unit. |
| `kind` | `ResourceMountKind` | `Configuration`, `Secret`, or `Volume`, carried from the manifest. |
| `source` | string or null | Unresolved manifest source expression. Resolution and credential handling are gateway responsibilities. |

### `ProbeMapping`

| JSON field | Type | Contract |
|---|---|---|
| `role` | string | `readiness`, `liveness`, or `startup`. A role appears at most once. |
| `endpoint` | string or null | Manifest endpoint used by an HTTP, TCP, or gRPC probe. |
| `kind` | `ProbeKind` | Exactly one of `Http`, `Tcp`, `Exec`, `Grpc`, or `None`. |
| `value` | string or null | HTTP path or gRPC service name; null for TCP, exec, and disabled probes. |
| `command` | array of strings | Exec command arguments; empty for every non-exec probe. |

### `VolumeSpec`

| JSON field | Type | Contract |
|---|---|---|
| `name` | string | Name shared with its `MountBinding` and manifest mount. |
| `kind` | `ResourceMountKind` | Must be `Volume` in version 1. Configuration and Secret mounts are resolved inputs, not claims. |
| `size` | string | Non-empty requested storage size after a typed storage override. |
| `perReplicaClaim` | boolean | Must be `true` in version 1. Every replica owns an independent claim. |

A plan has a per-replica claim if and only if its workload is `StatefulSet`.

### `ServiceSpec`

| JSON field | Type | Contract |
|---|---|---|
| `name` | string | Stable logical service name. |
| `endpoint` | string or null | Manifest endpoint, or null for the governing headless service. |
| `port` | integer or null | Endpoint port, or null for a portless governing service. |
| `protocol` | string | Endpoint protocol; the generic governing service uses `tcp`. |
| `headless` | boolean | `false` for endpoint services; `true` for a governing service. |
| `governing` | boolean | `false` for endpoint services; `true` for the one service governing stable workload identity. |

Every manifest endpoint has exactly one non-headless, non-governing service. A plan with a
per-replica claim has exactly one additional portless service with `headless` and `governing`
both true; a plan without such a claim has none.

### `ExposureSpec`

| JSON field | Type | Contract |
|---|---|---|
| `name` | string | Stable exposure name. |
| `endpoint` | string | Public manifest endpoint. |
| `service` | string | Backing `ServiceSpec.Name`. |
| `scheme` | string | Endpoint scheme. |
| `protocol` | string | Endpoint transport protocol. |
| `port` | integer | Backing service port. |

There is exactly one exposure for each public endpoint and none for a private endpoint.

## `GenericPlanner` mapping

`GenericPlanner` is the default every resource kind inherits. Its output is pinned to the item
34/36 trait mapping (decision D17):

| Manifest or option fact | Version 1 plan result |
|---|---|
| `lifecycle.workload` | `workload.kind`, unchanged. |
| typed `Replicas`, otherwise `lifecycle.replicas` | `workload.replicas`. |
| `lifecycle.stopGraceSeconds` | `workload.stopGraceSeconds`; default 30 seconds. |
| `artifact` | `container.artifact = "self"`; platform artifact identity stays outside the plan. |
| endpoint | One `PortBinding` and one endpoint `ServiceSpec`, in declaration order. |
| public endpoint | One `ExposureSpec` backed by its endpoint service. |
| Configuration or Secret mount | One `MountBinding`; no `VolumeSpec`. The gateway resolves its source. |
| Volume mount | One `MountBinding`, one sized per-replica `VolumeSpec`, `StatefulSet`, stable identity, and one headless governing `ServiceSpec`. Manifest generation defaults a Volume-mounted resource to `StatefulSet`; the planner rejects a contradictory manifest. |
| environment | Manifest values copied, then the three frozen identity values overwritten by authoritative planning context values. |
| readiness, liveness, or startup probe | One `ProbeMapping` with the same role, endpoint, and mechanism. |
| workload kind | The exact O30 `ReadinessGate` above. |
| compiler hints | Empty map. |

The checked-in [`KindMatrixTests` fixtures](../libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/tests/Fixtures/plans/README.md)
pin representative Web, Database, generic Volume, DaemonSet, and Job outputs. These fixtures are
the contract set vendored into `cohesion-platforms`, allowing every platform compiler to run the
same conformance inputs without loading a resource-area assembly.

## Per-platform realization semantics

A selected gateway must either preserve every required semantic below or refuse the plan during
its pre-gather validation. Silently dropping a field, changing a gate, or degrading an unsupported
workload is nonconforming.

The Local and in-process implementations belong to the Cohesion ApplicationModel family. The
Docker and Kubernetes compilers and controllers are implemented in `cohesion-platforms`; this
repository defines their shared input contract, not their platform object models.

| Plan concern | Local process | In-process | Docker (`cohesion-platforms`) | Kubernetes (`cohesion-platforms`) |
|---|---|---|---|---|
| `container.artifact = self` | Resolve and supervise the resource apphost. | Invoke the rooted resource entry point under its own ambient `ResourceContext` and adopt the returned host. | Resolve the resource's image and create its container realization. | Resolve the resource's image and place it in the workload pod template. |
| workload | One supervised resource apphost; the controller enforces supported lifecycle and restart semantics or rejects the plan. | `ProcessHost` compiles only its supported, composable subset; an unsupported kind, replica shape, or non-composable artifact is rejected. | Long-running container realization; `Job` is run once and `DaemonSet` is one container in the Docker topology. Unsupported replica semantics are rejected rather than collapsed. | Create the exact `Deployment`, `StatefulSet`, `DaemonSet`, or `Job` named by `workload.kind`; carry replicas and platform restart behavior without kind or area dispatch. |
| ports and services | Allocate and persist loopback ports; publish the observed endpoint values to the resource environment. No separate service object is required. | Allocate loopback endpoints in the member's ambient context. No process-global endpoint state or separate service object is used. | Bind container ports on the application network and provide stable container-network discovery for logical services. | Create one Service per endpoint; a governing headless service supplies StatefulSet identity and Service DNS. |
| mounts and volumes | Materialize mounts below `.cohesion/<application>/<resource>/`; persistent claim paths are stable across restarts. | Carry mounts in the ambient context as handles or directories rooted under the composite's mounts. | Use named volumes per claim, configuration data, and tmpfs for Secret mounts and the bootstrap credential. | Use StatefulSet `volumeClaimTemplates` sized from `VolumeSpec`, ConfigMaps for Configuration mounts, and Secrets for Secret mounts and the bootstrap credential. |
| exposures | Report the allocated local endpoint; no external platform object is synthesized. | Keep endpoints on member loopback; no external platform object is synthesized. | Publish the requested host ports for `ExposureSpec` entries. | Compile public exposures to the configured Ingress or LoadBalancer strategy, backed by the named Service. |
| probes | Run gateway-side probes against the allocated process endpoint; the ready line begins probing but is not readiness proof. | Combine nested host `Started` state with the same loopback probe semantics. | Probe through a gateway-created loopback-only host binding to the container port, even for private endpoints. | Map readiness, liveness, and startup probes one-for-one onto the workload container; pod Ready plus Endpoints produces `Running`. |
| environment | Supply `ContainerSpec.Environment` as process environment variables together with resolved runtime inputs. | Put identity, endpoints, mounts, settings, references, and credentials in the per-invocation ambient context because environment variables are process-wide. | Supply ordinary values as container environment and resolved sensitive inputs through tmpfs. | Supply ordinary values through ConfigMap-backed configuration and resolved sensitive inputs through Secret volumes. |
| readiness gate | Wait on `workload.gate.terminals` and succeed only for `workload.gate.satisfying`. | Same gate over each adopted member's observed lifecycle. | Same gate over the controller's observed container lifecycle. | Same gate over observed workload and pod state; surface container exit code and not-Ready/restart observations. |
| graceful stop | Signal, wait `stopGraceSeconds`, then kill if necessary. | The outer budget caps each adopted host's graceful stop. | Use `docker stop -t <stopGraceSeconds>`. | Set `terminationGracePeriodSeconds` to `stopGraceSeconds`. |
| hints | Interpret only registered Local hints; unknown keys warn once and are ignored. | Interpret only registered in-process hints; unknown keys warn once and are ignored. | Interpret only registered Docker hints; unknown keys warn once and are ignored. | Interpret only registered Kubernetes hints; unknown keys warn once and are ignored. |

Resolved mounts, bootstrap credentials, observed dependencies, and platform options are compiler
inputs beside the plan. They are deliberately excluded from the plan hash; credential rotation
must not appear as desired-state drift. Kubernetes annotates compiled objects with
`cohesion.io/plan-hash` computed from the plan alone.

## Build diagnostics

After a resource's plan is computed and validated, `Build()` emits one informational line to
standard error in resource declaration order. Standard output remains reserved for the
machine-readable describe and render documents. An area planner names the complete path to the
selected platform compiler:

```text
appa-database: Database planner → kubernetes compiler
```

When no area planner exists, the inherited fallback is explicit and has no implied area-specific
step:

```text
worker: GenericPlanner
```

The planner label is supplied by the planned resource, and the compiler label comes from the
selected gateway identity. These lines are diagnostics only: they are not serialized into the
plan, do not affect its hash, and cannot substitute for validation. A fallback must never be
silent.
