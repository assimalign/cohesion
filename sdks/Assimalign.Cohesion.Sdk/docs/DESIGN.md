# Assimalign.Cohesion.Sdk Design

## Purpose and boundaries

The base SDK owns behavior shared by all Cohesion SDK consumers. Runtime service
composition remains in `*.Hosting`; the SDK contributes only MSBuild properties,
items, targets, generated source, and build diagnostics. Its task assembly runs
on .NET 10 and generated source must remain trim- and NativeAOT-safe.

The authoritative developer-experience decisions for this area include §4.1, R3, T21, and T24
in [`docs/DEVELOPER_EXPERIENCE_DESIGN.md`](../../../docs/DEVELOPER_EXPERIENCE_DESIGN.md).

## Project defaults

The base SDK supplies the following defaults to every `Assimalign.Cohesion.Sdk`
and `Sdk.<Area>` consumer, including Gateway:

| Property | Default |
| --- | --- |
| `OutputType` | `Exe` |
| `TargetFramework` | `net10.0` |
| `LangVersion` | `Preview` |
| `EnablePreviewFeatures` | `true` |
| `ImplicitUsings` | `disable` |
| `Nullable` | `enable` |
| `IsAotCompatible` | `true` |

Each base default uses `Condition="'$(PropertyName)' == ''"` and is overridable.
The conditions honor command-line `-p:` global properties. A consumer's
`Directory.Build.props` and csproj body are evaluated later and override the
defaults through MSBuild's last-assignment-wins rule; those consumer values are
not visible when the conditions run.

`Targets/Assimalign.Cohesion.Sdk.Defaults.props` is the first import in the base
`Sdk.props`, before `Microsoft.NET.Sdk`'s `Sdk.props`. Microsoft's props already
default `OutputType` to `Library`, so an emptiness condition after that import
would never select `Exe`. The shipped TFM is a literal because the repository's
`build/Targets/Build.TargetFramework.props` is not shipped. That repository-side
source, this literal, and the SDK's `KnownFrameworkReference` TFMs must move together.

This implements developer-experience design §4.1: `Directory.Build.props` carries
identity only (`CohesionOrganization`, `CohesionContainerRegistry`, `VersionPrefix`).
A resource needs its SDK declaration, Cohesion items/properties, and `Program.cs`.
Library-style base-SDK consumers pulled in through `CohesionProjectReference`
declare `<OutputType>Library</OutputType>` explicitly; the
`Sdk.Gateway/tests/TestProjects/GatewaySmokeSupport` fixture is an in-repo example.

Two constraints apply:

- A consumer's `TargetFrameworks` (plural) is invisible at props time. Setting it
  leaves both `TargetFramework` and `TargetFrameworks` populated, so Microsoft's
  `Sdk.targets` does not set `IsCrossTargetingBuild=true`. Resource executables
  never multi-target (design R3).
- On the latest TFM, `Microsoft.NET.Sdk.Common.targets` forces
  `LangVersion=Preview` whenever `EnablePreviewFeatures=true`. The base SDK's
  language default is therefore effectively redundant: override `LangVersion`
  together with `EnablePreviewFeatures` (for example, `14.0` and `false`).

Gateway preserves two stricter assignments: `Targets/Sdk.Gateway.props` sets
`IsAotCompatible=true` unconditionally after the consumer's
`Directory.Build.props`, and `Sdk/Sdk.targets` forces `OutputType=Exe` after
the csproj body. These Gateway assignments are not conditional base defaults;
in particular, a csproj `OutputType=Library` cannot turn a gateway into a library.

## Generated identifier contract

Every generated C# accessor uses the same identifier transformation. Hyphen, underscore, dot,
colon, and other non-alphanumeric separators delimit segments; the first character of each segment
is upper-cased and the remaining characters are preserved. Four-character application segments
retain the established application spelling (`appa` becomes `AppA`). Empty results become `Value`,
and a leading digit is prefixed with `_`. Consequently `platform-configuration-store` always emits
`PlatformConfigurationStore` in resource and gateway generated surfaces.

## StronglyTypedSettings contract

`CohesionAppSettingsClass` is the sole opt-in switch and supplies the generated
root class name. `CohesionAppSettingsNamespace` optionally supplies its
namespace and otherwise resolves to the consumer's `RootNamespace`. With no
class property, no settings input is collected, no task runs, and no generated
source enters `Compile`.

The generator reads `appsettings*.json` in ordinal-ignore-case path order with
JSON comments and trailing commas enabled and merges those files into one
compile-time shape. Null values do not erase a known type, integer/floating-point
overlays widen to `double`, and incompatible scalar/object/array shapes fail the
build instead of producing order-dependent source. It emits:

- `#nullable enable` before all generated declarations;
- a public root class and public nested classes;
- nullable annotations on scalar, object, collection, and collection-element
  members discovered from JSON;
- the required `System`, collection, globalization, and Cohesion configuration
  `using` directives; and
- a public instance `Bind(IConfiguration)` method on the root type.

The binder is generated as explicit code. Every scalar read calls
`IConfiguration.GetEntry` with the complete colon-delimited path and consumes an
`IConfigurationValue`; numeric and Boolean conversion uses statically known
parsers, and arrays use the numeric paths discovered from the JSON shape while
preserving existing elements for paths that are not configured. It does not call
`ConfigurationBinder`, generic conversion helpers, reflection, dynamic
activation, or runtime code generation. Missing leaves preserve current property
values, so property initializers remain valid defaults.

The JSON files define the generated array shape. Runtime providers may override
the discovered numeric indices, but indices that exist only at runtime are not
added to the generated shape. Nested arrays and arrays that mix scalar and object
elements are rejected because they do not have one statically known element
contract.

Generation is incremental over a fingerprint of the class, namespace, and input
paths plus MSBuild timestamp tracking for each settings file and the task
assembly. Outputs live beneath the target-framework-specific intermediate path,
and Clean removes generated settings source and fingerprints even after opt-out.

## Resource command advertisements

For an orchestration-enabled executable, `CohesionCreateResourceManifest` consumes
`@(CohesionCommand)` and writes the item identities as the manifest's `commands` string
array. These are the kinds accepted by the area's default control plane; declarations,
owners, keys, and payloads remain runtime application-model data. Command items accept
no custom metadata. Names are trimmed, deduplicated with ordinal comparison, and sorted
ordinally so equivalent declarations produce identical manifests. Empty names fail the
build. No items produces the existing empty array; disabled executables produce no
manifest or command output.

Area SDK props supply defaults before the consumer body, preserving ordinary MSBuild
`Include`/`Remove` behavior. Database advertises `database.add-database` and
`database.add-principal`; ConfigurationStore advertises `configurationstore.set-value`
and `configurationstore.remove-value`. The manifest remains `cohesion/resource/v1` with
bare strings, as required by developer-experience design §7.

## SDK pin agreement contract

Before package-reference collection and before build preparation, the base SDK
walks from `MSBuildProjectDirectory` toward the filesystem root and inspects the
nearest `global.json`. No file means no validation. The tooling-only escape
`CohesionSkipSdkPinCheck=true` suppresses the validation target.

The recognized SDK identities are the base SDK plus these nineteen extensions:

- `Assimalign.Cohesion.Sdk.ApiManager`
- `Assimalign.Cohesion.Sdk.ConfigurationStore`
- `Assimalign.Cohesion.Sdk.Database`
- `Assimalign.Cohesion.Sdk.EmailHub`
- `Assimalign.Cohesion.Sdk.EventHub`
- `Assimalign.Cohesion.Sdk.Gateway`
- `Assimalign.Cohesion.Sdk.IdentityHub`
- `Assimalign.Cohesion.Sdk.IoTHub`
- `Assimalign.Cohesion.Sdk.LoadBalancer`
- `Assimalign.Cohesion.Sdk.LogSpace`
- `Assimalign.Cohesion.Sdk.MediaHub`
- `Assimalign.Cohesion.Sdk.MessageHub`
- `Assimalign.Cohesion.Sdk.NatGateway`
- `Assimalign.Cohesion.Sdk.NotificationHub`
- `Assimalign.Cohesion.Sdk.Rezolvr`
- `Assimalign.Cohesion.Sdk.Scheduler`
- `Assimalign.Cohesion.Sdk.SecretStore`
- `Assimalign.Cohesion.Sdk.VpnGateway`
- `Assimalign.Cohesion.Sdk.Web`

Every recognized entry that is present under `msbuild-sdks` is compared with
ordinal string equality. The base SDK's value is the expected value when it is
present; otherwise the first recognized identity in ordinal name order is the
anchor. This is agreement, not comparison with the repository's canonical
package version: a full pin block using `10.0.1-preview.3.local` is valid.

When `sdk.version` is present, its numeric SDK version must be at least
`10.0.300`. A lower or malformed version fails with COHSDK002. JSON comments and
trailing commas are accepted to match the .NET SDK's `global.json` format.

## Diagnostics

| Code | Severity | Contract |
| --- | --- | --- |
| COHSDK001 | Error | A resource reference cannot target a project with `CohesionApplicationModel` disabled. |
| COHSDK002 | Error | All present recognized Cohesion SDK pins must agree exactly and the pinned .NET SDK must be valid and at least `10.0.300`. |
| COHSDK004 | Warning or error | Resource packing requires a valid digest-pinned image when `CohesionImageRequired=true`; otherwise a manifest-only package is warned. |
| COHSDK008 | Error | Enabling the application model requires `OutputType=Exe`. |
| COHSDK009 | Error | Resource properties must use the current kind's lower-case prefix. |

Package-backed tests under `tests/` are the acceptance boundary. They build
consumer fixtures from packed SDKs so validation includes NuGet SDK resolution,
imports, generated source compilation, and incremental MSBuild behavior.
