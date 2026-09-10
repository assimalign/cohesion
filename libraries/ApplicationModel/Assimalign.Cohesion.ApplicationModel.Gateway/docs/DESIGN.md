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
- **`ApplicationGatewayOptions.Controllers`** — the registered-first domain override seam;
  framework options ship with an empty collection.
- **`InMemoryResourceStateManager`** — the public, race-free reference implementation of
  `IApplicationResourceStateManager` for gateway authors.
- **`LocalGateway`** (+ `LocalGatewayOptions`) — the default gateway for local development,
  which realizes each resource as a supervised child process.
- **`IGatewayStoreClient`**, **`IApplicationTrustGateway`**, **`ITrustedIssuerProvider`**, and
  **`IResourceCommandCredentialProvider`** — the Hosting-free seams for late-bound inputs,
  per-application trust, and resource-scoped command dispatch credentials.
- **`IImageRealizer`** — the platform seam that turns a digest-pinned image reference into an
  `IContainerImageArtifact` without putting registry or platform I/O in the base algorithm.
- Internal pieces: `ResourceControlContext`, `LocalResourceResolver`, port and mount
  materializers, the probe runner, `LocalGatewayProcessSupervisor`, `LocalProcessStateStore`,
  `LocalProcessSignal`, `LocalPlanController`, `ExecutableArtifact`.
- **`UseLocalGateway()`**, **`AddExecutable(...)`**, and **`AddContainer(...)`** builder
  extensions.

It references the Core-only `Assimalign.Cohesion.ApplicationModel`, the thin
`Assimalign.Cohesion.SecretStore.Client` and
`Assimalign.Cohesion.ConfigurationStore.Client` protocol packages, IdentityModel's
`JsonWebToken` package, and the DataProtection/ProtectedData primitives. The two client
references are the signed O13 exception: a gateway may depend on the narrow `<Area>.Client`
surface needed for resolution, but **never** on an area's `*.Hosting` package. The package is
`IsAotCompatible` / AOT-gated — no reflection, no `Microsoft.Extensions.*`. Publishing the
concrete reference state manager is the signed-off, narrowly scoped exception to the repository's
interface-first default; the interface remains the control-plane contract.

## The generic algorithm (why the base owns it)

`ApplicationGateway` implements `IApplicationGateway` **explicitly** and forwards to
strongly-typed `protected` hooks (`GatherAsync`, `ResolveInputsAsync`, `Controllers`, `State`,
`StartObserverAsync`),
per the repo's interface-first-with-guided-base convention. Every gateway — local, Docker,
Kubernetes — is the *same* algorithm with different hooks, so it is written once:

1. **Validate** every `descriptor.Plan` during `Build()` by asking registered controllers first,
   then the platform controller, whether `CanRealize(plan, out reason)`. Failure names the resource
   and gateway before any gather or target contact.
2. **Order** `model.Descriptors` topologically (depth-first post-order; the model is already
   validated acyclic at build time, so no cycle guard is needed here).
3. **Gather** each resource's artifact via `GatherAsync` (local → an executable path; container
   gateways → a pre-built image). Gathering locates/validates; it never builds.
4. **Start the single observer** (`StartObserverAsync`) — the only writer of observed status
   into `State`. Controllers only *apply* desired state; they never own steady-state.
5. **Reconcile in dependency order**: after dependencies have satisfied their initial gate,
   resolve `ResourceInputs { Mounts, BootstrapCredential, ApplicationTrustKey }` for this pass,
   compile the immutable plan plus artifact, inputs, and observed dependencies, and call
   `ReconcileAsync`. A controller may set `Skipped`; its dependents become `Skipped` while
   independent resources continue.
6. **Gate once from the plan**: call `WaitForStateAsync` with
   `plan.Workload.Gate.Terminals` and a per-resource budget, then admit the resource exactly when
   `Gate.Satisfying` contains the reached state. Long-running kinds satisfy on `Running`; a Job
   satisfies on `Stopped`. `Degraded` is observational and never re-gates an admitted dependent.
7. **Stop or uninstall in reverse order**: `StopAsync` calls each controller's non-destructive
   runtime stop hook and retains persistent objects; `UninstallAsync(model)` calls `DeleteAsync`.
   A failure during initial realization still rolls back whatever was partially applied.

This is why a `Failed`, cleanly `Stopped`, or never-ready dependency can never deadlock the
graph: readiness is a **plan-owned terminal-set** membership wait with a budget, not an ordinal
state comparison. Deployment, StatefulSet, and DaemonSet plans terminate on
`{Running, Failed, Stopped}` and satisfy only on `Running`; Job plans terminate on
`{Stopped, Failed}` and satisfy only on `Stopped`. This is the cohesion-side contract referenced
by the `cohesion-platforms` rule 2 amendment; that sibling repository is not edited here.

`LocalPlanController` keeps compilation pure:
`Compile(plan, artifact, inputs, observedDependencies, options)` only produces the local desired
process/environment description. Port allocation, claim-directory creation, mount-file writes,
and process launch happen afterward in the apply path. Platform compilers must preserve the same
boundary: no target reads, file writes, or process/network operations during compilation.

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

- **Resolution**: manifest-backed resources launch the exact `artifact.apphost`; the local
  gateway never falls back to the managed assembly DLL. A plain or orchestration-disabled
  executable can launch only through `AddExecutable(name, path, options)`, with an explicit
  readiness probe or per-resource stdout marker.
- **Endpoints**: each endpoint gets a loopback port persisted in
  `.cohesion/<application>/.state/ports.json`. The gateway injects the frozen `ResourceEnvironment`
  endpoint contract (caller values win), publishes the allocated endpoints atomically with the
  first `Running` transition. Once a manifest-referenced dependency reaches `Running`, the
  dependent receives `COHESION_DEPENDENCY_<RES>_<EP>_{URL,HOST,PORT,SCHEME}` exclusively from
  that dependency's observed endpoints. Optional references never gate startup: they inject only
  when the target is already `Running`, and otherwise inject nothing. An explicit C# `DependsOn`
  edge remains ordering-only.
- **Probes**: HTTP (exactly 200 succeeds; 404/405 fail startup immediately), TCP, and exec are
  gateway-side and AOT-safe. Missing manifest probe roles use the resource's default control-plane
  endpoint and role route (`<path>/readyz` for startup/readiness, `<path>/livez` for liveness);
  explicit `none` disables a role. The canonical
  `cohesion-resource: ready` stdout line starts manifest probing but is not readiness proof.
  `AddExecutable` may instead use its configured marker as the whole readiness signal.
- **Supervision**: stdout and stderr are piped with a `[resource-name]` prefix. A failed liveness
  attempt moves `Running` to `Degraded` with the probe detail. Three consecutive failures restart
  under `OnFailure` (default), `Always`, or `Never`, with 1-second exponential backoff capped at
  30 seconds and five restart attempts. A restart observes
  `Degraded → Stopping → Starting → Running`; dependents are never re-gated.
- **Mounts**: resources receive `COHESION_MOUNT_<M>_PATH` rooted at
  `.cohesion/<application>/<resource>/<mount>`. POSIX directories/files use exact 0700/0600
  modes. A Composite plan's flattened `<member>-<mount>` claim instead receives the outer carrier
  `COHESION_MOUNT_<COMPOSITE>_<MEMBER>_<M>_PATH`, pointing to the same resource-rooted claim;
  inward remapping belongs to `ProcessHost`.
  Windows files are CurrentUser DPAPI ciphertext; this makes no ACL claim. The
  `ResourceMount` reader in `Assimalign.Cohesion.Hosting.Resources` decrypts that file for the child while
  retaining the raw path for tools that require one.
- **Bootstrap credential**: every local reconcile receives a fresh ASCII bearer credential.
  The apply path writes it with the same private file discipline at
  `.cohesion/<application>/<resource>/.state/bootstrap.token` and injects only
  `COHESION_BOOTSTRAP_TOKEN_PATH`; the credential is never placed directly in the environment.
  A later reconcile rotates the file atomically.
- **Shutdown**: Windows launches use `CreateNewProcessGroup`; POSIX launches use `setsid` when
  the host provides it, with a best-effort `setpgid` fallback. On Windows, a fresh manual-reset
  event named by `COHESION_STOP_EVENT` is primary and targeted `CTRL_BREAK` is the console
  fallback; POSIX sends `SIGTERM` to the process group when isolation succeeded. The gateway
  waits the manifest's `lifecycle.stopGraceSeconds` (30 seconds by default;
  `LocalGatewayOptions.StopGrace` is the
  fallback for opaque executables), then force-kills the whole group/tree. A process that exits
  during grace becomes `Stopped`; one that requires escalation becomes `Failed(forced)`.
- **Known POSIX limitation**: process grouping depends on the host-provided `setsid` command with
  a best-effort `setpgid` fallback; no RID-native launch helper is shipped.
- **Recovery**: `.cohesion/<application>/.state/owner` records the gateway identity and
  `.cohesion/<application>/.state/<resource>/pid` records PID, process start time, executable path,
  process-group ownership, and the Windows stop-event name. A new gateway independently verifies
  PID + start time + executable before adopting a live child and rebuilds readiness from probes.
  A foreign owner is refused before process realization unless this invocation carries
  `--adopt`. Observed state is rebuilt on every gateway start; the PID file is verified recovery
  metadata, never persisted lifecycle truth. `UninstallAsync` also verifies an orphan before
  stopping it and removes the resource's persisted mounts and port allocation.
  `--restart-orphans` (or `LocalGatewayOptions.RestartOrphans`) instead gracefully stops each
  verified child and launches a fresh attempt.

## Item #968 — late-bound inputs, application trust, and opaque workloads

### Gateway-owned mount-source resolution (signed O13)

`ApplicationGateway.ResolveInputsAsync` is the sole default resolver. It runs after a
resource's dependencies pass their initial gate and runs again on every reconcile; resources
consume already-resolved inputs and never pull orchestration data themselves. Each
`MountBinding.Source` is handled as follows:

- an omitted source resolves to empty content (or an empty gateway-created Volume claim);
- `literal:<value>` is UTF-8 content and is valid only for a Configuration mount;
- `parameter:<name>` resolves from the application parameter document, then from
  `ApplicationGatewayOptions.Parameters`; generated gateway startup applies repeatable
  `--parameter name=value` values last. The default document is
  `.cohesion/<application>/parameters.json` unless `ParameterFile` is explicit. Windows reads
  CurrentUser-DPAPI ciphertext; on POSIX the gateway enforces mode 0600 before reading.
  A Volume cannot declare a parameter or any other source;
- `<resource>:<key>` must name an unambiguous declared dependency that is `Running` and has an
  observed control-plane endpoint. A Secret mount reads bytes from a `SecretStore`; a
  Configuration mount reads a namespace from a `ConfigurationStore` and encodes its
  ordinal-key-ordered values as JSON. A mismatched store kind is unresolved.

Missing parameters, malformed references, unavailable endpoints, client failures, and kind
mismatches produce a typed unresolved `ResourceMountInput` with the source-specific reason.
`LocalPlanController` refuses that input before applying the process; every platform controller
must preserve the same no-partial-realization rule.

For a store reference, the gateway uses the source resource's one credential for the current
reconcile pass and calls
`IGatewayStoreClient`. Its default implementation delegates only to the thin
`SecretStore.Client` and `ConfigurationStore.Client` protocol packages. This is the narrow,
signed O13 boundary: sharing those client contracts avoids hand-rolling their wire protocols,
while `*.Hosting` remains forbidden and no store client enters a resource runtime.

A manifest endpoint can designate a Secret mount through its `Certificate` field. That path
uses `IGatewayStoreClient.ReadCertificateAsync` and accepts a PEM leaf when the store supports
it. Item 31's CA enrollment and leaf issuance are **not available yet**; an unavailable leaf
remains a named, typed unresolved input. This package neither self-issues a certificate nor
silently substitutes another source.

### Per-application trust and rotating credentials

The gateway owns one ECDSA P-256 signing key per application and gateway identity. The private
PKCS#8 value stays under `.cohesion/<application>/trust/<gateway>/` and is encrypted at rest with
`Assimalign.Cohesion.Security.DataProtection`, scoped by application and gateway; the protecting
key ring is DPAPI-backed on Windows and uses 0700 directories/0600 files on POSIX. DataProtection
is only the at-rest primitive — ECDSA is the signer — and the private key never enters a
resource, export, or trusted-issuer document. `IGatewayTrustKeyRepository` is the platform
override for a durable native Secret; the protected local repository is the default.

The public-only JWK carries `kty=EC`, `crv=P-256`, `alg=ES256`, `use=sig`, and an RFC 7638
thumbprint `kid`. The same JWK is supplied through `ResourceInputs.ApplicationTrustKey`, emitted
as `COHESION_APPLICATION_TRUST_KEY` for a local child, and published as
`ApplicationExportDocument.TrustKey`; platform compilers receive it through the same immutable
inputs. The application's own public key is always retained in its trusted-issuer snapshot.

Every reconcile creates one fresh ES256 bootstrap JWT per resource with issuer = application,
subject = gateway, audience = target resource, a new `jti`, and bounded `iat`/`nbf`/`exp`
(24 hours maximum). Every gateway-side call to that resource during the pass reuses the same
credential. Rotation means replacing the credential on every reconcile, not exposing
the signer or placing the token value directly in an environment variable. The carrier contract
is:

| Realization | Bootstrap credential carrier |
|---|---|
| Local out-of-process | Private `.state/bootstrap.token` file named by `COHESION_BOOTSTRAP_TOKEN_PATH`; 0600 on POSIX, DataProtection ciphertext on Windows; atomically replaced. |
| In-process | Value on the per-invocation ambient `ResourceContext`. |
| Docker | tmpfs file, implemented by the Docker platform compiler. |
| Kubernetes | Secret volume, implemented by the Kubernetes platform compiler. |

Peer verification keys are `TrustedIssuer` records exposed through
`ITrustedIssuerProvider`. The gateway reads `trusted-issuers.json` from **that application's
own** `SecretStore`, using its observed running endpoint or, only in Development, its declared
local port and an audience-bound bootstrap credential. The gateway refreshes that snapshot before a
reconciliation pass when the store is already observed and immediately after its own store first reaches
`Running`, so later remote resources in the same pass can use newly loaded peer grants. The topological
walk prefers the application's own store among otherwise independent roots while preserving its declared
dependencies, preventing declaration order from placing first-pass remote resolution ahead of trust.
Loading or storing a peer grant may fall back to the application-local trusted-issuers document
only in Development. A definite not-found means that no peer grants exist yet outside
Development; Development keeps its local fallback until the store contains a replacement
document. Every other malformed or unavailable store read outside Development is fatal.
Platform gateways resolve a
stable native endpoint for one-shot trust commands through `TryResolveOwnSecretStoreEndpoint`;
`trust-add` outside Development requires that own store. Refreshing peers never removes the
application's self key.

The one-shot gateway command modes are the low-level surface used by later CLI wrappers:

- `--mode trust-issue --developer <name>` prints a short-lived ES256 developer token with
  audience `cohesion-export`, a new `jti`, and an eight-hour maximum lifetime. Rotating the
  application key invalidates credentials signed by the old key.
- `--mode trust-add --peer <peer> --from <file-or-https-uri>` validates that the peer name exactly
  matches the exported application and that the export contains its public JWK, then writes the
  grant to the **verifying application's** own `SecretStore`. A remote source requires an
  authorization-configured `IControlPlaneClient`; plaintext HTTP is limited to Development
  loopback. Only Development may use the local fallback document.

### Opaque executable and container entry points

- `AddExecutable(...)` is the explicit LocalGateway-only escape hatch for a plain apphost or
  native executable with no manifest/control plane. It requires either an enabled readiness
  probe or a stdout ready marker and can configure endpoints, probes, environment, restart
  policy.
- `AddContainer(...)` turns an opaque OCI image into a generic deployment plan. The image must
  end in an immutable `@sha256:<64-hex-digits>` digest, and the caller must declare at least one
  endpoint and an enabled readiness probe; the options also carry startup/liveness probes,
  environment, and restart policy.
- `IImageRealizer.RealizeAsync(resource, imageReference, cancellationToken)` is the platform
  boundary that converts that digest-pinned reference to an `IContainerImageArtifact`. This
  package defines the seam but does not pull, build, load, or publish images; Docker and
  Kubernetes gateways own those operations and their corresponding plan controllers.

## Items #964 and #965 — external resolution, composition, and control-plane serving

This package implements the gateway-side lifecycle seams for items 23 and 23a. The hosting-free
HTTP server and client live in `Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane`;
platform Kubernetes importer/exposure remains item 37.

### Resolver controller and lifecycle

`ApplicationGateway` owns one internal `ExternalResourceController`. Controller selection remains
deterministic: application-registered overrides first, then the external controller, then the
gateway's platform controllers. A plan with the `cohesion.external=true` hint is accepted by the
external controller and receives an artifact-free `ExternalResourceArtifact`; `GatherAsync` is
not called while it stays external. A Development `--realize` removes that hint, so the selected
platform gathers and reconciles the resource like any other planned workload.

On every reconcile pass the controller invokes the resource's `IExternalResourceResolver` with
its immutable `ExternalResourceDeclaration` and the optional
`ApplicationGatewayOptions.ControlPlaneClient`:

- a resolved result containing all referenced endpoint names becomes `Running` and publishes the
  observed endpoints;
- an optional unresolved result becomes `Skipped`, allowing independent branches to continue;
- a required unresolved result becomes `Starting`; the common plan gate waits the resource's
  readiness budget (60 seconds by default), then records `Failed`, aborts startup, and marks its
  dependents `Blocked` if the resolver has not produced a terminal/satisfying state;
- a result missing any referenced endpoint becomes `Failed`. Its detail names the missing
  endpoint and the expected/observed manifest hashes and export schema versions;
- differing non-null manifest hashes or reported export schema versions with compatible endpoints
  remain `Running` and append the stable `ManifestDrift` warning to the state detail. A changed
  detail is observable even when the lifecycle remains `Running`.

Resolver calls are one-shot within a reconcile pass; a resolver that needs polling owns that work.
The readiness budget is the gateway gate after resolution, not an implicit resolver retry loop.
`StopAsync` and `DeleteAsync` only set the local external observation to `Stopped`; they never ask
the peer application to stop or delete its resource.

The built-in `Gateway(...)` resolver remains transport-neutral. It returns an actionable
unresolved result when no `IControlPlaneClient` was configured, and otherwise asks that client for
an `ApplicationExportDocument`. The ControlPlane package supplies the authenticated HTTP client,
while static and file clients remain usable for tests and offline workflows.

### One gateway session for several models

`ApplicationGateway` implements `IMultiModelApplicationGateway`; its existing single-model
`IApplicationGateway` methods delegate to singleton batches. A batch is validated before target
contact, preserves application declaration order and each model's topological resource order, and
starts exactly one observer for the complete collection. Reconcile follows that combined order;
stop and uninstall reverse both resource and application order.

The active-session key is `(ApplicationName, ResourceId)`. When a batch contains more than one
model, `ApplicationScopedResourceStateManager` projects every member through an
application-scoped state view so identical resource names/identifiers in two applications cannot
share lifecycle or endpoint observations. Multi-model observers obtain the correct view through
`GetApplicationState(model)`. A gateway cannot begin supervising a different model collection
until the active collection has stopped.

`IApplicationSet` itself lives in the Core-only contract package. It resolves each
`ApplicationDeclaration` at run start (local `--mode describe`, file export, or a supplied
control-plane client), then invokes this batch seam for `Run`, `Apply`, or `Teardown`. No
`Gateway.CreateModel` reflection or runtime assembly scan is involved. SDK-generated
`Applications.<Name>` declarations and ControlPlane composition are supplied by the Gateway SDK;
Kubernetes ConfigMap import/export remains outside this package's implementation.

After each successful start or reconcile pass, the base gateway creates one validated,
source-generated `ApplicationExportDocument` per active model from its observed endpoint state.
Its default publication hook atomically replaces
`.cohesion/<application>/export.json`; `LocalGatewayOptions.StateDirectory` supplies the local
root unless `ApplicationGatewayOptions.ExportDirectory` is set explicitly. Platform gateways can
override `PublishApplicationExportAsync` to publish the identical document through their native
control plane without changing the model or wire contract. Successful stop and teardown withdraw
the document through `RemoveApplicationExportAsync`, preventing a resolver from advertising
endpoints after their resources are no longer running.

When `ApplicationGatewayOptions.ControlPlane` is configured, the base gateway also owns one
control-plane instance per active application. Each instance receives that exact export document,
the application-scoped observed-state view, and the application's own trusted-issuer provider.
`LocalGateway` binds it to a stable port from the persisted local port store; the ControlPlane
package publishes `.cohesion/<application>/control-plane.json` only after the listener and model
are ready, and removes that metadata when the listener stops. The server obtains a target
resource's current bootstrap token through `IResourceCommandCredentialProvider`; developer export
tokens cannot mutate the command surface.

## Testing posture

The generic algorithm and the state manager are unit-tested deterministically (a `TestGateway`
with recording controllers asserts validation-before-gather, controller precedence, topological
reconcile/input ordering, `Skipped` continuation, plan-derived gates, stop versus uninstall,
failure→`Blocked`+throw, and readiness-timeout; the state manager asserts terminal-set returns
for `Running`/`Failed`/`Stopped`/timeout, cancellation propagation and cleanup, non-gating
`Degraded`, race-free set-before-subscribe, observed endpoints, and the event). The generic
algorithm also verifies that post-`Running` degradation does not re-gate dependents. Real
child-process spawning is exercised by a co-located, BCL-only test apphost. `LocalGateway` tests
cover persisted ports and contract environment, default and explicit HTTP readiness (including
404 fail-fast), TCP and exec probes, liveness degradation/restart/backoff, observed dependency
injection and startup gating, optional absence, Composite re-export names, mount materialization,
prefixed stdout/stderr, `AddExecutable` marker readiness, the full exponential-backoff sequence,
graceful and forced stop classification, PID-file re-attachment, and explicit orphan restart.
Item #968 tests additionally cover literal/parameter/store resolution, typed certificate
unavailability, fresh audience-bound ES256 credentials, per-application persisted trust keys,
bounded developer tokens, digest enforcement for `AddContainer`, and parameter CLI precedence.

## Non-goals

- Building container images or talking to Kubernetes — those are the platform gateway packages
  (`…Gateway.Kubernetes` / `…Gateway.Docker`).
- Owning DI/Config/Logging — that stays inside each `{Resource}.Application` runtime.
- A full drift-reconcile loop with server-side apply and informer resync — that is specified for
  the Kubernetes gateway; the local gateway's supervisor is the equivalent for processes.
