# Template package design

## Direction and scope

The authority is [developer-experience design §4, §8, O20 and O26](../../../../docs/DEVELOPER_EXPERIENCE_DESIGN.md).
The package implements design item 39 and replaces the retired `extensions/dotnet` scaffold.
It owns eleven short names: `cohesion-app`, `cohesion-landing-zone`, `cohesion-gateway`,
`cohesion-composite`, `cohesion-web`, `cohesion-spa`, `cohesion-database`, `cohesion-secretstore`,
`cohesion-configurationstore`, `cohesion-identityhub` and `cohesion-rezolvr`.

There is no runtime API or assembly reference surface to document under `docs/Assembly/`.
The package is L1 tooling; its output consumes the corresponding resource-area SDKs and frameworks.
The CLI wrapper belongs to item 40, and further resource-area templates are deferred until their
SDKs mature. The retired generic resource and zone template names are not aliases.

## Content and topology

`src/content/<shortName>/.template.config/template.json` is the template engine entry point.
Each identity is unique and each template supports `-n`. The only framework choice is `net10.0`.
The package project disables default item discovery, so embedded executable programs never compile
into the packaging assembly. NuGet's default exclusions are disabled to retain `.gitignore`.

The application and landing-zone trees follow the tracked `cohesion-examples` scaffolds at `f43e6fa`,
except for the gateway programs and store mounts described under *Gateway composition and providers*,
which moved ahead of that snapshot. The application tree is flattened to the repository root. The landing-zone source has 25 projects;
`--topology federated` removes the root application-set gateway and substitutes only the solution,
six gateway configuration files and README from `.topologies/federated`. This keeps the shared
programs and project references identical. The package contains exactly 37 content project files.
Each of those projects includes `Properties/launchSettings.json`: exactly one profile named
after the project, with `commandName: Project` and `COHESION_ENVIRONMENT: Local`. Template
name substitution updates the profile name together with the csproj filename. Both topology
outputs carry the profiles for their emitted projects; federated output also removes the root
gateway's profile. The three landing-zone APIs use `appsettings.Local.json`.

Local is the developer-machine environment. Development is an ordinary deployed environment
with strict security behavior, and the framework's unset default stays Production. These
profiles select Local for `dotnet run` and IDE launch. An explicit command-line environment
overrides the profile; use `dotnet run --no-launch-profile` when selecting an environment
through inherited shell variables because launch profiles override inherited variables.
The CLI handles that switch when a shell environment variable is supplied. The root landing-zone
application set also appends Local only when its existing explicit-argument and variable checks
find no environment, preserving direct executable launch convenience.
The root application's set uses the generated `Applications.AppA`, `AppB` and `AppC` members;
the landed example's `Appa`, `Appb` and `Appc` spellings do not compile against the current SDK.

The landing-zone domains each import their ancestor `Directory.Build.props` and explicitly set
`identity`, `networking`, `platform`, `appa`, `appb` or `appc`. Every template also writes root
application identity. Single-project templates derive the default from the lowercased first name
segment through template symbols; `--applicationName` overrides it. Project filenames and C#
namespaces use the original `-n` casing. Resource-name symbols normalize punctuation to hyphens.

## SDK ownership and opt-in

The base SDK owns `OutputType`, `TargetFramework`, `LangVersion`, `EnablePreviewFeatures`,
`ImplicitUsings`, `Nullable` and `IsAotCompatible`. None is emitted in projects or identity props.
SDK-owned defaults supersede the older consumer example in `.claude/rules/build-system.md`.
The template package and test harness inherit repository configuration and remain AOT-compatible.

O26 enables every application/landing-zone resource because it is referenced. Standalone resource
templates explicitly set `disabled`, with an area-specific comment naming the ApplicationModel
package and its three generated outputs. Their programs use no generated `Resource` surface.
The plain database owns a local data directory and SQL schema; the plain SPA serves copied static
content. Both can compile before orchestration is enabled.

Gateways declare neither `CohesionApplicationModel` nor `CohesionResourceReferencesAreRuntime`:
both were redundant in the landed examples, and the SDK controls their effective values. Composites
use `Sdk.Gateway`, a resource name, `Local;InProcess` and the explicit InProcess flag. Landing-zone
root and Networking gateways select only Local; the others select Local and InProcess. This differs
from §4.5(a)'s target Docker selection because the container providers are not released.

Programs retain the landed composition scope. Zone gateways currently realize their Database/API/SPA
subset while retaining the other references. Generic area configuration and full provider-backed
production realization remain separate work; builds here do not claim runtime readiness of every domain.

## Gateway composition and providers

`Sdk.Gateway` generates `Manifests`, `Externals`, `Applications`, the provider catalog, `UseGateway`
and `Gateway.CreateBuilder`, but no per-resource `Add<Member>()` verb. Every gateway program composes
its resources with the area's hand-written verb over the generated manifest, for example
`builder.AddWeb(Manifests.AppAApi)` or `builder.AddDatabase(Manifests.AcmeDatabase)`, and would use
`builder.AddResource(Manifests.X)` for a kind without an ApplicationModel package. The programs pass
no options because the landed scaffolds configured none; an options instance such as
`new WebResourceOptions { Replicas = 2 }` replaces the retired `Action<TOptions>` callback.

Nothing is registered by convention. Before the provider seams, a gateway silently used its
application's own SecretStore as the certificate authority and trust store, resolved every
`<store>:<key>` mount against a SecretStore or ConfigurationStore resource, and sent telemetry to its
application's own LogSpace. The templates now reproduce that behavior explicitly, in the two places that
compose the Platform application:

| Gateway | Package references | Registrations |
| --- | --- | --- |
| `Example.Platform.Gateway` | SecretStore and ConfigurationStore `ApplicationModel.Orchestration` | `UseSecretStore(secrets).AsCertificateAuthority().AsTrustStore()`, `UseConfigurationStore(configuration)`, `Providers.Telemetry = ResourceTelemetrySink.FromResource(logs)` |
| `Example.Gateway` (application set) | the same two packages | the same three registrations, by resource name, in the `AddApplication(Applications.Platform, platform => ...)` callback, because a member's describe output carries no providers |

Each package is a `CohesionPackageReference` pinned to `$(CohesionVersion)`, the version the SDK itself
was packed at, so a scaffold never names a package version. No other gateway composes a store:
Identity and Networking hold no SecretStore, and the zone gateways leave their SecretStore referenced
but unrealized.

Cross-application store sources are a documented follow-up (owner decision 4): `Build()` rejects a
mount whose `<source>:<key>` names another application's store. The landed IdentityHub read its TLS
bundle and signing keys, and the VPN gateway its keys, from `platform-secretstore`; those mounts are
now the parameters `identity-hub-tls`, `identity-signing-keys` and `networking-vpn-keys`, supplied with
`cohesion parameter set <name> --stdin --project <gateway>` (plus `--app identity` or `--app networking`
for the root application set) or `--parameter name=value`. The TLS source stays explicit rather than
falling back to the gateway development authority, so the IdentityHub endpoint behaves the same in
every environment; an unset parameter leaves the resource unrealized with an error naming it. The
Platform SecretStore references stay, so the identity and networking gateways still bind
`Externals.PlatformSecretStore`. No mount reads that store now; the reference remains a declared
dependency only.

Feed-free assertions keep this true without a package feed: no gateway program calls a parameterless
`Add<Member>()` verb, a gateway references an Orchestration package exactly when its program calls
that package's `Use<Area>` verb, every package reference is pinned to `$(CohesionVersion)`, and every
`<source>:<key>` mount names a store of the consuming resource's own application.

## Packing and versioning

`PrepareCohesionTemplates` copies tracked content into the intermediate output before NuGet gathers
package files, then replaces `__COHESION_PACKAGE_VERSION__` in staged `global.json` files with
`$(CohesionVersion)`. `__COHESION_DOTNET_SDK_VERSION__` and `__COHESION_DOTNET_ROLL_FORWARD__`
come from the repository root `global.json`. The canonical package version and the 20 pin values
therefore agree at pack time; `.local` package pins are rejected. Source files are never rewritten.
The centralized branding import adds the standard NuGet icon.

Every template includes nuget.org mapped to Cohesion packages and `*`, plus a placeholder organization
feed mapped to the generated name prefix. It contains no machine feed or authentication material.
The generated guard uses split regular-expression spellings so it also passes the repository guard
that scans this template source. `.cohesion/` and `parameters.json` remain ignored in every scaffold.

## Acceptance lifecycle

An xUnit v2 fixture packs the package into its own workspace, installs it with `dotnet new install`,
and uninstalls it on disposal. Each process receives that workspace's `DOTNET_CLI_HOME` and
`NUGET_PACKAGES`; neither the user's template hive nor global package cache is used. Workspaces live
under `_out/verify-39/w` to stay within the repository and limit Windows path depth. An empty
`Directory.Build.targets` at the workspace boundary prevents repository targets leaking into consumer
builds; it sets no project defaults. Logs and generated projects remain available for inspection.

Feed-free assertions cover every installed template and both topology choices, complete inventory pins,
explicit identity and opt-in, seven-property absence, authored programs, source hygiene and root files.
They also cover all 37 source launch profiles, profile-name substitution, emitted Local settings
filenames and the root gateway profile's removal from federated output.
Name replacement and explicit gateway identity receive separate coverage. Feed-backed tests first
repeat those assertions, then substitute only the SDK pin values and the isolated smoke feed config.
They build the generated tree and check manifest presence for enabled projects and absence for disabled
ones. Package requirements are derived per template from the actual content SDKs and their area owner
props, framework packs for the host RID, the existing Gateway consumer closure (the `_requiredPackageIds`
list in the Gateway SDK's `ConsumerWorkspace.cs`), and each `CohesionPackageReference` in the content
together with its repository project's `CohesionProjectReference` closure.
The .NET SDK's `ProcessFrameworkReferences` also downloads targeting packs for every registered
Cohesion framework to support transitive references. Gateway builds additionally request all
registered runtime packs for the host RID. Discovery reads the base SDK's actual registrations
for this additional closure; no consumer properties are injected to suppress those downloads.

A discovery-time Fact subclass reports two permitted skips: absent exact-version packages and a base
SDK package lacking `Targets/Assimalign.Cohesion.Sdk.Defaults.props`. This keeps feed-free release
validation useful. A complete feed turns consumer build errors into failures, never skips.

## CI and inventory integration

`tooling-templates.yml` prepares 28 library packages: the 23-package Gateway consumer closure, the
VpnGateway ApplicationModel, and the two store Orchestration packages with their SecretStore and
ConfigurationStore clients. Web, Web.Routing and the Database.Client closure left the list when
Sdk.Gateway stopped injecting area clients. The workflow then uses the shared build action to pack
canonical SDK/framework packages for the runner's RID and build/test this package. The action's `SkipLibraries` bootstrap preserves the prepared libraries.
Package publication remains exclusively in the repository release workflow. Besides the template,
SDK, build and framework-producer paths, the workflow triggers on `libraries/ApplicationModel/**` and
the area `ApplicationModel` and `ApplicationModel.Orchestration` packages, because the gateway programs
call those APIs directly.

The shared release module must add this package and exact-path exclusions for all 37 template content
projects, removing the two obsolete legacy exclusions. Those shared edits are handed to the orchestrator
and rehearsed in a scratch module. The root solution and README updates follow the same protocol.

## Concrete resource composition (T10 / O34)

Filler resource programs hold the concrete Hosting builder, apply area verbs before Build(),
and retain the concrete application with await using before calling RunAsync. This keeps host
execution and disposal available while the root interfaces carry only area contracts.


## Phase 29 Database composition migration

Database programs now capture `AddSql((context, engine) => ...)` intent, register
the server through that engine builder's deferred `AddServer` factory, and
identify deferred provisioning with the engine name. One application Build
constructs and owns the engine and nested server; the program disposes the
application. The standalone template still uses its ordinary local data path.
This migration changes composition only; it adds no ApplicationModel declarations,
manifests or resource control planes. Template acceptance explicitly builds all
five emitted Database programs because resources-only changes do not trigger the
Templates workflow.
