# Assimalign.Cohesion.Sdk.Gateway Design

## Design intent

`Sdk.Gateway` turns an ordinary executable project and its
`CohesionResourceReference` items into a generated, AOT-compatible Cohesion application
gateway. MSBuild sees the application graph; generated C# supplies the fluent
application-specific API; runtime packages realize the resulting model. There is no
runtime XML parsing, assembly scan, reflection-based provider discovery, or Roslyn
generator.

The gateway is also a resource. It is always orchestration-enabled, has resource kind
`Composite`, and produces the same `cohesion/resource.json`, embedded manifest, image,
and manifest-package assets as another enabled resource. This permits an outer gateway
or application set to reference it through its control plane.

## Package and framework boundary

The SDK package has the normal Cohesion MSBuild SDK layout:

```text
Sdk/Sdk.props
Sdk/Sdk.targets
Targets/Sdk.Gateway.props
Targets/Sdk.Gateway.targets
Tasks/Assimalign.Cohesion.Sdk.Gateway.Tasks.dll
```

It imports `Assimalign.Cohesion.Sdk`, but it does not create or reference an
`Assimalign.Cohesion.App.Gateway` framework. The orchestration plane consists of
`Assimalign.Cohesion.ApplicationModel`,
`Assimalign.Cohesion.ApplicationModel.Gateway`,
`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane`, optional provider packages, and the
narrow client packages required to resolve protected mount sources and deliver commands.

The Gateway props must set `CohesionAutoIncludeAppFramework=false` before importing the
base SDK. Otherwise the base props add `Assimalign.Cohesion.App` during evaluation and
the claimed NuGet-only boundary is false. The sanctioned in-process mode adds explicit
framework references for the resource areas it actually nests; out-of-process gateways
reject resolved `*.Hosting` assemblies with `COHGW001`.

## Evaluation and target ordering

MSBuild imports `Sdk.props` before the consumer project body and `Sdk.targets` after it.

The base SDK first supplies the seven [project defaults](../../Assimalign.Cohesion.Sdk/docs/DESIGN.md#project-defaults)
before importing Microsoft's SDK props. Gateway inherits these defaults and preserves
its stricter behavior: `Targets/Sdk.Gateway.props` unconditionally sets
`IsAotCompatible=true` after the consumer's `Directory.Build.props`, and its targets
force `OutputType=Exe` after the consumer body. The base default supplies the initial
`OutputType` value. Library-style base-SDK dependencies such as `GatewaySmokeSupport`
explicitly declare `OutputType=Library`.

That ordering is load-bearing:

1. Gateway props register the task, defaults, resource-kind metadata, known typed area
   mappings, and fixed orchestration dependencies.
2. The consumer declares `CohesionApplicationName`, `CohesionGateways`, and
   `CohesionResourceReference` items.
3. Gateway `Sdk.targets` unconditionally restores `OutputType=Exe`,
   `CohesionApplicationModel=enabled`, and `CohesionResourceKind=Composite` before it
   imports the base targets. The base resource targets compute their enabled state while
   being imported; forcing these values afterward would be too late.
4. The base `CohesionResolveResourceReferences` target builds project references only
   far enough to obtain their manifests. Resource assemblies are not compilation
   references outside the explicit in-process exception.
5. `CohesionCreateResourceVerbs` depends on that target, reads project and package
   manifests, and writes `Gateway.g.cs` before `CoreCompile`.
6. The base resource task independently writes the gateway's Composite manifest and
   lifts same-application member endpoints, mounts, and lifecycle constraints.

`CohesionCreateResourceVerbs` must use an explicit dependency on
`CohesionResolveResourceReferences`. Giving both targets only
`AfterTargets=ResolveProjectReferences` does not establish their relative order.

## Generated source

The task consumes JSON manifests, not referenced compilations. It validates identifiers,
application boundaries, duplicate providers, provider metadata, and protected
mount-source mappings before emitting source.

Gateway members use the base SDK's shared generated-identifier contract: non-alphanumeric
separators delimit Pascal-cased segments, `appa` becomes `AppA`, and
`platform-configuration-store` becomes `PlatformConfigurationStore`. The rule applies uniformly to
`Applications`, `Externals`, `References`, manifest members, resource verbs, and provider members.

The generated application name is used in two ways:

- `[assembly: CohesionApplicationAttribute("appa")]` is metadata for build and tooling
  inspection.
- `Gateway.CreateBuilder(args)` directly calls
  `Application.CreateBuilder(ApplicationName.Parse("appa"), args)`. Runtime code never
  reads the attribute to discover identity.

Same-application manifests produce `Add*` methods. A typed area mapping supplies its
area-owned options type, `DescriptorType`, and `Add<Area>` method; an unmapped area uses
`ResourceOptions` and `IApplicationBuilder.AddResource`. Boundary-crossing manifests
produce `ExternalResourceDeclaration` values instead of realization verbs. A referenced
Composite gateway additionally produces an `Applications.<Name>` declaration resolved
through that gateway's control plane.

Web, Database, ConfigurationStore, SecretStore, IdentityHub, Rezolvr, and LogSpace
mappings return their area's `I<Area>ResourceDescriptor` and accept its
`<Area>ResourceOptions`. Database, ConfigurationStore, SecretStore, IdentityHub, and
Rezolvr retain typed command verbs on generated `Add*` results. Web preserves its
existing typed planner; LogSpace provides typed options and a named telemetry-sink
descriptor without command verbs. Custom mappings that omit `DescriptorType` keep
`IApplicationResourceDescriptor`. In-process binding is applied after constructing
the descriptor, and the original typed descriptor is returned.

`CohesionGatewayClientKind` maps both mount sources and command targets to narrow
client packages. A manifest with a non-empty `commands` array requires its kind's
client even when no mount uses it: Database maps to Database.Client and
ConfigurationStore maps to ConfigurationStore.Client. The manifest reader accepts
only non-empty command-kind strings; payloads remain in runtime model declarations.

Provider dispatch is generated only from `@(CohesionGatewayProvider)`. The item contract
is:

| Metadata | Meaning |
| --- | --- |
| `Name` | Stable command-line and environment selection name. |
| `GatewayType` | Concrete `IApplicationGateway` implementation constructed by generated code. |
| `OptionsType` | Provider-specific options accepted by the configuration overload. |
| `CommandLineApplyMethod` | Optional fully-qualified static `void Apply(OptionsType, string[])` hook. |
| `RequiresJit` | Whether including the provider makes the gateway executable ineligible for NativeAOT. |

`UseGateway(args)` honors the builder's parsed request, then `COHESION_GATEWAY`, then the
Local-only Local default. Unknown or unavailable providers fail with the generated
set of valid names. The overload taking `Action<CohesionGatewayProviders>` executes only
the callback for the selected provider. After common arguments are applied, generated code
calls the selected provider's optional `CommandLineApplyMethod` with the original, unfiltered
`args`; provider packages use that AOT-safe hook for switches such as Kubernetes
`--context` and `--kubeconfig`. The method must be public and static with the exact signature
`void Apply(OptionsType options, string[] args)`. Providers without the metadata retain their
existing constructor path. Both construction paths install the ControlPlane
resolver client in every mode and the server factory only for `Run` and `Apply`; `Describe`,
`Render`, and `Bootstrap` never bind a listener.

## Restore-time dependency contract

Fixed dependencies and platform selection are known during project evaluation and can be
added to the NuGet restore graph normally. Manifest-derived dependencies are different:
the referenced package manifest becomes available only after restore, and a project
manifest becomes available only after `ResolveProjectReferences`. A task that discovers
an ApplicationModel or client at either point cannot add a `PackageReference` to the
already-created `project.assets.json`.

The minimal honest implementation is therefore:

1. Inject only the fixed ApplicationModel/Gateway packages and explicitly selected
   provider packages during evaluation.
2. Keep the finite bootstrap explicit: the seven Web, Database, ConfigurationStore,
   SecretStore, IdentityHub, Rezolvr, and LogSpace ApplicationModel packages plus the
   three SecretStore, Database, and ConfigurationStore client packages, all pinned at
   `$(CohesionVersion)`. Every gateway restores this set, regardless of its manifests.
3. Validate the manifest-derived requirement set after resolution and fail with an
   actionable diagnostic when a required compile dependency is unavailable.
4. Preserve the existing Web mapping and add rows and injected packages for areas whose
   ApplicationModel ships a typed descriptor with command verbs, plus LogSpace for typed
   options on the telemetry sink. Every other area stays on the generic
   `ResourceOptions`/`AddResource` path. This seven-area bootstrap is the shipped shape;
   T11's manifest-derived injection remains the documented future shape. Keeping the
   list finite and explicit avoids injecting every current and future area or client
   package, which would hide the restore-order defect, bloat every gateway, and weaken
   the package boundary.

The recommended complete fix is a producer-authored, restore-visible dependency
descriptor. A manifest package should place only its orchestration ApplicationModel and
required mount clients into the NuGet dependency graph; it must never acquire an area
runtime or `*.Hosting` dependency. A project reference must expose the equivalent edges
through the project restore graph. After manifests resolve, `Sdk.Gateway` validates that
the restore-time declaration matches `applicationModel`, resource kind, and mount-source
facts in the JSON. This preserves one restore/build invocation and keeps the manifest
itself portable. If that producer contract is not accepted, require explicit dependency
metadata or PackageReferences from the gateway project and diagnose omissions; do not
attempt an implicit second restore from a build target.

## Provider package injection

`CohesionGateways` is the sole platform-package selection input. Local comes from the
always-injected Gateway runtime. InProcess belongs to its separate Cohesion package.
Docker and Kubernetes belong to cohesion-platforms and are referenced at
`CohesionPlatformsVersion`. The SDK must not infer a provider package from a resource
kind, generate kind-by-platform packages, or name a platform implementation type.

Provider `buildTransitive` props contribute the item used for source generation. A
`RequiresJit=true` contribution sets `CohesionGatewayRequiresJit`; under
`CohesionGatewayAot=auto`, any such contribution disables AOT for the complete executable
regardless of which provider is selected at run time. Explicit `true` or `false` remains
a deliberate consumer override and should produce a visible build diagnostic.

## In-process exception

Outside in-process mode, every resource project reference remains manifest-only:
`ReferenceOutputAssembly=false` and `OutputItemType=CohesionResourceManifest`. In-process
mode turns resource project references into real assembly references during evaluation,
before `ResolveProjectReferences`, and adds the App, Web, and Database frameworks during
that same evaluation. It also sets
`ValidateExecutableReferencesMatchSelfContained=false`; the base SDK supplies enabled
Debug resource executables with the design's self-contained host-RID defaults. Manifest-package
resources cannot be nested because they have no local executable binding.

These SDK-owned defaults make the examples' temporary consumer bridge removable. Delete
`<CohesionResourceReferencesAreRuntime>true</CohesionResourceReferencesAreRuntime>` from
`examples/single-app/Acme.Gateway/Acme.Gateway.csproj` and from the Identity, Platform, and
Zones/AppA, AppB, and AppC gateway projects under both `examples/k8s` and
`examples/k8s-federated`. The root `Directory.Build.targets` is also removable in full:
its Debug `SelfContained`, `RuntimeIdentifier`, and
`ValidateExecutableReferencesMatchSelfContained` properties are now SDK defaults.

This exception remains guarded by explicit `CohesionGatewayInProcess=true`, `COHGW001`,
content-root isolation, and an ambient `ResourceContext` per invocation. Generated bindings use
`AppContext.BaseDirectory/cohesion/resources/<resource-name>`. The SDK disables ordinary
transitive content copying and remaps each composable project resource's declared output/publish
content beneath that root. It also collects the selected closure's managed, native, satellite,
and RID-specific runtime files without admitting assets from disabled or non-composable projects.
Because that selection occurs after restore, those child package identities do not become entries
in the gateway lock file, cannot participate in gateway-wide NuGet version solving, and cannot
contribute child-only `buildTransitive` behavior. Completing those semantics requires the
restore-visible producer descriptor described above. This exception does not authorize an
`App.Gateway` framework or allow Hosting references in an ordinary out-of-process gateway.

## Current integration gaps

The following are explicit integration gates, not behavior the SDK may emulate with
stubs:

- The provider contribution contract has no Docker or Kubernetes implementation in this
  repository.
- `CohesionPlatformsVersion` has no repository-wide version source beyond the SDK's
  temporary Cohesion-version default.
- Web, Database, ConfigurationStore, SecretStore, IdentityHub, Rezolvr, and LogSpace
  provide typed area ApplicationModel mappings; other areas use the generic path.
- First-restore manifest dependency metadata has not landed; the finite dependency
  bootstrap described above is transitional.
- The Gateway SDK suppresses the base SDK's implicit `Assimalign.Cohesion.App` reference
  before the base props import; no Gateway shared framework exists.
- Three-OS package-boundary smoke CI is wired, but release `validate-consumer` coverage
  must still consume the exact publication artifact before release publication.

## Verification requirements

Tests must consume packed SDKs from an isolated NuGet source and pin both the base and
Gateway SDK identities. Coverage includes always-enabled behavior, Composite manifest
lifting, project and manifest-package references, boundary-aware generated source,
duplicate/unknown providers, AOT propagation, `COHSDK001`, `COHGW001`, and the first clean
restore with no pre-existing assets file or global-packages cache entry.

CI must also publish and execute a self-contained Gateway smoke application on Windows,
Linux, and macOS, including the in-process host lifecycle path and isolated content roots. A
Linux NativeAOT publish runs the same bounded in-process smoke. Release publication must consume
the exact package artifact from `Pack-Release.ps1`, build and run the consumer, and only then
permit package publication.

## Application image gather

`CohesionPublishImages` publishes referenced source resources through the base SDK's singular
`CohesionPublishImage` and reads sources-absent resources from restored manifest packages'
`cohesion/image.json`. Package resources are provisionally the `Pinned` boundary; source
projects use incremental `Rebuild`. The design lists freshness among resource-project inputs,
so final ownership of package freshness remains an open design question.

The output beside the gateway publish directory is `application.images.json`, governed by the
frozen platforms `IMAGE_INDEX.md`: `schema=cohesion/images/v1`, non-empty `application` from
`CohesionApplicationName`, and `images` in declaration order. Each entry contains the resource
image fields without `schema`; duplicate resource ownership is an error and an empty array is valid.
Archives are digest-verified, copied beneath `images/<ordinal>/`, and their relative paths
rewritten against the application document. Paths cannot escape that directory. Missing
archives are not silently ignored; a registry-only entry omits `archive` entirely.

Only an active gateway whose selected provider set is solely `InProcess` uses one composite
entry owned by the gateway's own `CohesionResourceName`. Selecting InProcess among other
providers, or merely enabling `CohesionGatewayInProcess`, does not suppress member entries:
Docker/Kubernetes plans resolve `ArtifactRef.Self` per member at run time. Mixed active
InProcess sets append the composite after the member entries; indirect project resources follow
direct declarations in the existing manifest-closure order.

The SDK uses the existing single-RID OCI producer, configuration/capability AOT decisions,
and release-only push policy documented in the base SDK. It adds no workflow push step.
