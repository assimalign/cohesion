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

The application and landing-zone trees follow the tracked `cohesion-examples` scaffolds at `f43e6fa`.
The application tree is flattened to the repository root. The landing-zone source has 25 projects;
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
props, framework packs for the host RID, and the existing Gateway consumer closure.
The .NET SDK's `ProcessFrameworkReferences` also downloads targeting packs for every registered
Cohesion framework to support transitive references. Gateway builds additionally request all
registered runtime packs for the host RID. Discovery reads the base SDK's actual registrations
for this additional closure; no consumer properties are injected to suppress those downloads.

A discovery-time Fact subclass reports two permitted skips: absent exact-version packages and a base
SDK package lacking `Targets/Assimalign.Cohesion.Sdk.Defaults.props`. This keeps feed-free release
validation useful. A complete feed turns consumer build errors into failures, never skips.

## CI and inventory integration

`tooling-templates.yml` prepares the 34-package Gateway closure plus five landing-zone ApplicationModel
packages, then uses the shared build action to pack canonical SDK/framework packages for the runner's
RID and build/test this package. The action's `SkipLibraries` bootstrap preserves the prepared libraries.
Package publication remains exclusively in the repository release workflow.

The shared release module must add this package and exact-path exclusions for all 37 template content
projects, removing the two obsolete legacy exclusions. Those shared edits are handed to the orchestrator
and rehearsed in a scratch module. The root solution and README updates follow the same protocol.

## Concrete resource composition (T10 / O34)

Filler resource programs hold the concrete Hosting builder, apply area verbs before Build(),
and retain the concrete application with await using before calling RunAsync. This keeps host
execution and disposal available while the root interfaces carry only area contracts.
