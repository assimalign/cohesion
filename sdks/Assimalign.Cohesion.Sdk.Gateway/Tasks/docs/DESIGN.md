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

It imports `Assimalign.Cohesion.Sdk.ApplicationModel` unconditionally and then
imports `Assimalign.Cohesion.Sdk`, but it does not create or reference an
`Assimalign.Cohesion.App.Gateway` framework. The orchestration plane consists of
`Assimalign.Cohesion.ApplicationModel`,
`Assimalign.Cohesion.ApplicationModel.Gateway`,
`Assimalign.Cohesion.ApplicationModel.Gateway.ControlPlane`, optional provider packages, and the
`<Area>.ApplicationModel` package of each referenced resource project's area. No area client
package is part of it: the gateway delivers commands through its generic control-plane command
client. Store, certificate-authority, and trust providers come from opt-in
`<Area>.ApplicationModel.Orchestration` packages the consumer references and registers itself,
and a credential issuer or caller authenticator is the consumer's own `builder.Providers`
assignment.

The Gateway props retain `CohesionAutoIncludeAppFramework=false` before importing the
base SDK, matching the switch understood by resource-area SDKs. The base SDK itself
adds no framework reference. The sanctioned in-process mode adds explicit App and
resource-area framework references for the resources it actually nests;
App supplies Connections as part of its kernel because generated resource
accessors and every area hosting module depend on it. Out-of-process gateways
reject resolved `*.Hosting` assemblies with `COHGW001`.

## Evaluation and target ordering

MSBuild imports `Sdk.props` before the consumer project body and `Sdk.targets` after it.

The base SDK first supplies the seven [project defaults](../../../Assimalign.Cohesion.Sdk/Tasks/docs/DESIGN.md#project-defaults)
before importing Microsoft's SDK props. Gateway inherits these defaults and preserves
its stricter behavior: `Targets/Sdk.Gateway.props` unconditionally sets
`IsAotCompatible=true` after the consumer's `Directory.Build.props`, and its targets
force `OutputType=Exe` after the consumer body. Until that target runs, the base SDK
follows Microsoft.NET.Sdk's `OutputType=Library` default. Library-style base-SDK
dependencies such as `GatewaySmokeSupport` therefore need no explicit output type.

That ordering is load-bearing:

1. Gateway props register the task, defaults, the Composite resource metadata, the provider
   package map, and fixed orchestration dependencies.
2. The consumer declares `CohesionApplicationName`, `CohesionGateways`, and
   `CohesionResourceReference` items.
3. Gateway `Sdk.targets` unconditionally restores `OutputType=Exe`,
   `CohesionApplicationModel=enabled`, and `CohesionResourceKind=Composite`, imports
   the ApplicationModel SDK at `Version="$(CohesionVersion)"`, and then imports the
   base targets. The ApplicationModel import must precede Microsoft's targets because
   enabled-resource self-contained and RID defaults are consumed there.
4. The ApplicationModel `CohesionResolveResourceReferences` target builds project references only
   far enough to obtain their manifests. Resource assemblies are not compilation
   references outside the explicit in-process exception.
5. `CohesionCreateResourceVerbs` depends on that target, reads project and package
   manifests, and writes `Gateway.g.cs` before `CoreCompile`.
6. The ApplicationModel resource task independently writes the gateway's Composite manifest and
   lifts same-application member endpoints, mounts, and lifecycle constraints.

`CohesionCreateResourceVerbs` must use an explicit dependency on
`CohesionResolveResourceReferences`. Giving both targets only
`AfterTargets=ResolveProjectReferences` does not establish their relative order.

## Generated source

The task consumes JSON manifests, not referenced compilations. It validates identifiers,
application boundaries, duplicate providers, and provider metadata before emitting source.
It keeps its historical name, `CohesionCreateResourceVerbs`, although it no longer emits verbs.

Gateway members use the ApplicationModel SDK's shared generated-identifier contract: non-alphanumeric
separators delimit Pascal-cased segments, `appa` becomes `AppA`, and
`platform-configuration-store` becomes `PlatformConfigurationStore`. The rule applies uniformly to
`Applications`, `Externals`, `References`, manifest members, and provider members.

The generated application name is used in two ways:

- `[assembly: CohesionApplicationAttribute("appa")]` is metadata for build and tooling
  inspection.
- `Gateway.CreateBuilder(args)` directly calls
  `Application.CreateBuilder(ApplicationName.Parse("appa"), args)`. Runtime code never
  reads the attribute to discover identity.

Every captured manifest produces a `Manifests.<Name>` member, and nothing else is generated
per resource. Composition is explicit and hand-written: the gateway's `Program.cs` passes
`Manifests.<Name>` to the verb of the area's ApplicationModel package
(`builder.AddWeb(Manifests.OrdersApi, new WebResourceOptions { Replicas = 2 })`), to a
third-party ApplicationModel package's verb, or to `builder.AddResource(Manifests.<Name>)`
for a kind with no ApplicationModel in reach. The area verbs take their options as an
instance and return the area's `I<Area>ResourceDescriptor`, which carries the area's typed
command verbs (Database, ConfigurationStore, SecretStore, IdentityHub, and Rezolvr). The
generator therefore keeps no resource-kind table, emits no `CohesionGatewayResourceExtensions`
class, and has no untyped-fallback diagnostic: calling an area verb whose ApplicationModel the
gateway does not reference is an ordinary compile error. Boundary-crossing manifests produce
`ExternalResourceDeclaration` values. A referenced Composite gateway additionally produces an
`Applications.<Name>` declaration resolved through that gateway's control plane.

In-process bindings do not depend on the verb. When the in-process pass binds enabled,
composable project resources, `Gateway.CreateBuilder(args)` registers each binding by manifest
identity (`Manifests.<Name>.InProcess(...)`, keyed by application and resource name) and roots
each entry point with `DynamicDependency`. The in-process gateway looks the binding up from the
built resource's manifest, so a resource added through any verb over `Manifests.<Name>` is
colocated.

The manifest reader still validates the `mounts` and `commands` arrays (each command is a
non-empty kind string, or an object with a non-empty string `kind` and an optional boolean
`requiresInputResolver`; any other property or value type fails), but the SDK derives no package
from them. The gateway SDK does not act on `requiresInputResolver`: ApplicationModel's provider
validation enforces it at `Build()`, requiring a resolver in the declaring application's
`Providers.CommandInputs`. Commands reach resources through
the gateway's generic control-plane command client, and mount sources resolve through the
providers the gateway registers explicitly; payloads remain in runtime model declarations.

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
first `CohesionGateways` entry in declaration order when the apphost environment is Local.
An unset apphost environment is Local; an explicit deployed environment requires a selection.
The SDK keeps its `Local` provider default because `CohesionGatewayInProcess` remains opt-in;
composable templates explicitly put `InProcess` first and enable it. Unknown or unavailable providers fail with the generated
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
an ApplicationModel at either point cannot add a `PackageReference` to the
already-created `project.assets.json`.

The minimal honest implementation is therefore:

1. Inject only the fixed ApplicationModel/Gateway packages and explicitly selected
   provider packages during evaluation.
2. Derive the area set at evaluation time from what the gateway can already see: each
   `CohesionResourceReference` that names a `.csproj` is read with `File::ReadAllText` and its
   `Project Sdk="Assimalign.Cohesion.Sdk.<Area>"` attribute is the area, overridable by `Area`
   metadata on the reference. Exactly those areas' `<Area>.ApplicationModel` packages are
   injected, pinned at `$(CohesionVersion)` (repository projects inside cohesion). Nothing is
   injected for a manifest package, a missing project, a base-SDK project, or a referenced
   gateway. The same set drives the in-process framework references (`App` plus one
   `App.<Area>` per referenced area).
3. Inject nothing else by convention. No area client package is restored, and no package is
   inferred from a manifest's mounts or commands. A gateway that composes an area it does not
   reference as a project adds that area's ApplicationModel package itself to call the typed
   verb, or composes the manifest with `AddResource`; a gateway that resolves mount sources
   from a store references that store's Orchestration package and registers it in
   `Program.cs`. Deriving the injected set from project references keeps every gateway's
   restore graph as small as its composition without a second restore.

The recommended complete fix is a producer-authored, restore-visible dependency
descriptor. A manifest package should place only its orchestration ApplicationModel into the
NuGet dependency graph; it must never acquire an area runtime, an area client, or a
`*.Hosting` dependency. A project reference must expose the equivalent edges
through the project restore graph. After manifests resolve, `Sdk.Gateway` validates that
the restore-time declaration matches the `applicationModel` and resource kind in the JSON. This preserves one restore/build invocation and keeps the manifest
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
before `ResolveProjectReferences`, and adds the `App` framework plus `App.<Area>` for each
referenced area during that same evaluation. It also sets
`ValidateExecutableReferencesMatchSelfContained=false`; the ApplicationModel SDK supplies enabled
resource executables, in every configuration, with the design's self-contained host-RID
defaults and `DisableTransitiveFrameworkReferenceDownloads=true`. Manifest-package
resources cannot be nested because they have no local executable binding.

These SDK-owned defaults make the examples' temporary consumer bridge removable. Delete
`<CohesionResourceReferencesAreRuntime>true</CohesionResourceReferencesAreRuntime>` from
`examples/single-app/Acme.Gateway/Acme.Gateway.csproj` and from the Identity, Platform, and
Zones/AppA, AppB, and AppC gateway projects under both `examples/k8s` and
`examples/k8s-federated`. The root `Directory.Build.targets` is also removable in full:
its `SelfContained`, `RuntimeIdentifier`, and
`ValidateExecutableReferencesMatchSelfContained` properties are now SDK defaults.

Generated sources are build outputs. `Gateway.g.cs` is added to `Compile` by the target that
writes it, never as an evaluation-time item under the intermediate directory, so Visual
Studio shows no obj folder for it and the design-time build compiles it. `Clean` deletes it
with the other file writes and then regenerates it (without the in-process pass, whose member
assemblies are gone until the next build) so IntelliSense keeps the generated surface. A
design-time build (`DesignTimeBuild=true`) never builds the members, so it keeps an existing
`Gateway.g.cs` untouched and, when none exists, generates the surface without the in-process
pass; the real build regenerates it with the bindings.

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
  ship typed area verbs in their ApplicationModel packages; other areas compose through
  `AddResource`.
- First-restore manifest dependency metadata has not landed; the finite dependency
  bootstrap described above is transitional.
- Cross-application store sources (a mount `<source>:<key>` whose source is a resource of
  another application or a remote reference) are rejected at `Build()` for now. Supporting
  them is a follow-up for when the store resources mature.
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

`CohesionPublishImages` publishes referenced source resources through the ApplicationModel SDK's singular
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

Source-built manifests intentionally leave `artifact.image` empty: gather owns the lookup of
`ArtifactRef.Self` in this index; generated `Manifests.<Name>` members never acquire a digest.
`CohesionPublishImages` passes `CohesionImageRuntimeIdentifier` to every member while removing
the apphost's build `RuntimeIdentifier`. The dedicated image RID defaults to the explicitly
supplied `RuntimeIdentifier`, then `linux-x64`, before host-build defaults are applied. Local
container gateways request the node/engine RID and reuse the SDK fingerprint for freshness (O38).

Only an active gateway whose selected provider set is solely `InProcess` uses one composite
entry owned by the gateway's own `CohesionResourceName`. Selecting InProcess among other
providers, or merely enabling `CohesionGatewayInProcess`, does not suppress member entries:
Docker/Kubernetes plans resolve `ArtifactRef.Self` per member at run time. Mixed active
InProcess sets append the composite after the member entries; indirect project resources follow
direct declarations in the existing manifest-closure order.

The SDK uses the existing single-RID OCI producer, configuration/capability AOT decisions,
and release-only push policy documented in the ApplicationModel SDK. It adds no workflow push step.
