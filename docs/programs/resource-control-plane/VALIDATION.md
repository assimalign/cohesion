# Runtime state refactor validation

Validated locally on 2026-10-24. Both repositories use `feature/resource-runtime-manifests`.
Changes remain uncommitted and unpushed; no pull request or CI result is implied.

## Implemented scope

Hosting.Resources owns health contracts, validated runtime state contracts, local feature dispatch
and bootstrap admission. Hosting.Health is removed, with no compatibility package or namespace.
Resource manifests use `cohesion/resource/v2` without a command catalog. The SDK emits a management
binding; registered startup handlers supply capabilities and effective configured baselines.
The old state/initial-state MSBuild declarations and static runtime catalog registration are removed.

ApplicationModel remains independent of Hosting.Resources and preserves runtime JSON as an opaque
object. It composes dependencies and logical resource groups. All 16 non-Database typed descriptor
wrappers forward group membership during authoring; Scheduler returns the core descriptor directly.
Groups appear in composed documents and discovery responses without changing deployment identity,
Kubernetes namespaces, Docker networks, ownership, or workload hashes.

Web CORS and certificate selection have runtime adapters. Gateways discover authenticated catalogs,
persist conditional desired state and reconcile trusted process inventory. Explicit programmatic
commands retain their separate transport and authorization; resource manifests no longer advertise
their kinds or input-resolver requirements.

## Local checks

| Check | Result |
| --- | --- |
| 41 source test projects outside Database | 2,221 passed; zero failed or skipped |
| ApplicationModel | 413 passed, including opaque runtime metadata and resource groups |
| Hosting.Resources | 125 passed, including moved health tests and startup admission |
| Gateway / ControlPlane / InProcess | 310 / 54 / 55 passed |
| Web.Hosting / Web.Hosting.Management | 518 / 9 passed, including real HTTP CORS and TLS certificate selection |
| 16 area model suites after typed descriptor forwarding | Passed; Web includes the new before/after composition regression |
| App kernel and framework-list structure | Three selected cases passed |
| Focused SDK defaults, schema, retired inputs and packaged consumer | 26 passed; consumer builds, runs and packs |
| Current Gateway Release source build | Zero warnings and errors |
| Kubernetes | 258 passed |
| Docker | 104 passed; one daemon-dependent test skipped |
| Containers | 109 passed |
| Packaging and solution PowerShell tests after the final MSBuild grouping changes | 42 passed |
| Visual Studio .NET Framework MSBuild Web consumer restore | Passed with exact SDK pins and the standard user NuGet cache |
| NativeAOT win-x64 management guard | Publish and execution passed with trim/ILC warnings treated as errors |
| Private NuGet snapshot | 127 packages audited, including four App/Web Windows runtime packs; no retired Health artifacts or metadata |
| Web management package | No buildTransitive state-declaration targets |
| Release inventory | 226 libraries/resources, 21 SDK packs and 19 framework families validated |
| Dependency graph | Regenerated; final check matches project files |
| Touched project/props/targets files | XML parses; one item type per ItemGroup; split operations preserve item order and conditions |
| Git whitespace checks | Passed in both repositories |

The platform suites, SDK consumer and Visual Studio restore consumed the immutable private snapshot
`10.0.0-preview.1.runtimeconfig.20261024131405.local`, preserving the platforms' package-only boundary.
That snapshot was prepared before the final Model opacity, ControlPlane group visibility, typed
descriptor forwarding and source style refinements. Those changes were checked against current
source separately; existing package identities were not overwritten. The snapshot is a local
validation input, not a release of the final source tree.

The focused App checks cover kernel closure/package membership and framework-list layout. The full
SDK matrix and Database-dependent framework closure checks were not run. No live Kubernetes
ten-pod/HPA deployment or real Docker daemon end-to-end run was performed. Convergence is eventual
and dated against observed inventory; it does not guarantee simultaneous fleet cutover.

## Runtime discovery limit

A newly started gateway needs a reachable resource process to discover its contracts. With no live
catalog and no reachable process, it rejects management writes. Desired state remains durable;
a warm gateway can retain its validated catalog for temporarily unavailable processes in the same
deployment incarnation. This limit follows from runtime-owned startup discovery and is documented
in the [implementation contract](../../libraries/ApplicationModel/RESOURCE_MANAGEMENT.md).

## Database boundary

The owner explicitly kept every file under `resources/Database/` frozen and will migrate this branch
into the Database branch separately. The Database source diff is empty.

No Health shim, legacy resource/v1 reader, Database-specific SDK factory, or COHAM001 runtime
dependency exception is retained. Database still contains references and assumptions that require
that separate migration. Database and whole-repository builds may fail; making those builds pass
was deliberately excluded rather than introducing a workaround.
