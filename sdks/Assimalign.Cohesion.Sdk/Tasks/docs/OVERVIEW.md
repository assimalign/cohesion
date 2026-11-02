# Assimalign.Cohesion.Sdk

`Assimalign.Cohesion.Sdk` is Cohesion's general-purpose base MSBuild SDK. It
chains to `Microsoft.NET.Sdk` and can be used for ordinary libraries and
executables. It generates no application-model sources: a resource makes itself a
managed instance in its `Program.cs`, and a gateway declares its model in its own.

## Project defaults

The base SDK supplies conditional defaults for every base and layered consumer:

| Property | Default |
| --- | --- |
| `OutputType` | `Library` (from `Microsoft.NET.Sdk`; area SDKs select `Exe`) |
| `TargetFramework` | `net10.0` |
| `LangVersion` | `Preview` |
| `EnablePreviewFeatures` | `true` |
| `ImplicitUsings` | `disable` |
| `Nullable` | `enable` |
| `IsAotCompatible` | `true` |
| `DisableTransitiveFrameworkReferenceDownloads` | `true` |

Every ordinary default is conditional on an empty value. Command-line global
properties are honored, and later assignments in a consumer project can
override the props-time values. Resource-area SDKs set a private marker before
the base import so the pre-.NET default is `OutputType=Exe`.

The base SDK registers every Cohesion framework but includes none implicitly.
An executable that uses the base SDK opts into the hosting kernel with an
explicit `FrameworkReference`; resource-area SDKs add the kernel and their area
framework unless `CohesionAutoIncludeAppFramework=false`. The kernel carries
Connections and ApplicationModel.Hosting once because every area hosting module
and every area integration depend on them.

## Base build tooling

The package owns these behaviors:

- strongly typed settings generation from `appsettings*.json`, enabled by
  `CohesionAppSettingsClass`;
- name-only `CohesionProjectReference` resolution;
- agreement validation for Cohesion SDK pins in the nearest `global.json`;
- Cohesion framework registration without an implicit framework reference;
- common language, target-framework, AOT, and build defaults.

Strongly typed settings emit public, nullable-aware types and an AOT-safe
`Bind(IConfiguration)` method made from explicit configuration reads. No source
is generated when `CohesionAppSettingsClass` is absent.

## SDK pin validation

During restore and build, the base SDK walks upward to the nearest `global.json`.
When present, COHSDK002 requires all recognized Cohesion SDK pins that are
present to use the same exact version string and requires the pinned .NET SDK to
be `10.0.300` or newer. Local identities such as
`10.0.0-preview.1.local` are valid when the pins agree. Consumers without a
`global.json` are unchanged. `CohesionSkipSdkPinCheck=true` is reserved for
tooling that must inspect a temporarily inconsistent tree.

## Diagnostics

| Code | Severity | Meaning |
| --- | --- | --- |
| COHSDK002 | Error | Cohesion SDK pins disagree, or the nearest pinned .NET SDK is invalid or too old. |

See [DESIGN.md](./DESIGN.md) for import ordering and implementation contracts.
