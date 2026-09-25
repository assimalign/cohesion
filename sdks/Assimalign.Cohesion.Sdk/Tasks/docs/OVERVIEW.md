# Assimalign.Cohesion.Sdk

`Assimalign.Cohesion.Sdk` is Cohesion's general-purpose base MSBuild SDK. It
chains to `Microsoft.NET.Sdk` and can be used for ordinary libraries and
executables. Resource manifests, generated resource APIs, container images, and
orchestration belong to the separate
[`Assimalign.Cohesion.Sdk.ApplicationModel`](../../../Assimalign.Cohesion.Sdk.ApplicationModel/Tasks/docs/OVERVIEW.md)
package.

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
| `CohesionApplicationModel` | `disabled` |
| `DisableTransitiveFrameworkReferenceDownloads` | `true` |

Every ordinary default is conditional on an empty value. Command-line global
properties are honored, and later assignments in a consumer project can
override the props-time values. Resource-area SDKs set a private marker before
the base import so the pre-.NET default is `OutputType=Exe`.

The base SDK registers every Cohesion framework but includes none implicitly.
An executable that uses the base SDK opts into the hosting kernel with an
explicit `FrameworkReference`; resource-area SDKs add the kernel and their area
framework unless `CohesionAutoIncludeAppFramework=false`. The kernel carries
Connections once because generated resource accessors and every area hosting
module depend on it.

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

## Application-model boundary

An area SDK imports `Assimalign.Cohesion.Sdk.ApplicationModel` when the consumer
sets `CohesionApplicationModel=enabled`; Gateway imports it unconditionally.
The auxiliary SDK is resolved at the area SDK's exact `$(CohesionVersion)` and
does not need another `global.json` pin.

A project that enables the application model while using only the base SDK fails
with COHSDK011. Use a resource-area SDK or add the explicit import documented by
the ApplicationModel SDK. This guard prevents an enabled-looking project from
silently omitting manifests and generated source.

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
| COHSDK011 | Error | `CohesionApplicationModel=enabled` but the ApplicationModel SDK was not imported. |

Resource, manifest, certificate, and image diagnostics are documented by the
[ApplicationModel SDK](../../../Assimalign.Cohesion.Sdk.ApplicationModel/Tasks/docs/DESIGN.md#diagnostics).

See [DESIGN.md](./DESIGN.md) for import ordering and implementation contracts.
