# Assimalign.Cohesion.Sdk Design

## Purpose and boundaries

The base SDK owns behavior shared by all Cohesion SDK consumers. Runtime service
composition remains in `*.Hosting`; the SDK contributes only MSBuild properties,
items, targets, generated source, and build diagnostics. Its task assembly runs
on .NET 10 and generated source must remain trim- and NativeAOT-safe.

The authoritative developer-experience decisions for this area are T21 and T24
in [`docs/DEVELOPER_EXPERIENCE_DESIGN.md`](../../../docs/DEVELOPER_EXPERIENCE_DESIGN.md).

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

- a public root class and public nested classes;
- nullable properties for the discovered JSON leaves;
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
