# Overview

Repository-level documentation for the Cohesion mono repository. Coding standards and agent rules live in [`.claude/rules/`](../.claude/rules/) (auto-loaded by Claude Code; entry point [`.claude/CLAUDE.md`](../.claude/CLAUDE.md)).

**Go To**

- [Delivery Roadmap](./DELIVERY_ROADMAP.md) — delivery waves, initiative sequencing, and the L1/L2/L3 layering model
- [Service Layer Design](./SERVICE_LAYER_DESIGN.md) — high-level design for each service under `resources/`
- [Service Story Requirements](./SERVICE_STORY_REQUIREMENTS.md) — implementation requirements for service-level backlog stories
- [Developer Experience Design](./DEVELOPER_EXPERIENCE_DESIGN.md) — authoritative gateway, discovery, trust, and application-set contracts
- Build
  - [Cohesion Custom MSBuild Items](./build/MSBUILD_COHESION_PROPS.md) — `CohesionProjectReference`, `CohesionPackageReference`, code generation
  - [Common MSBuild Properties](./build/MSBUILD_COMMON_PROPS.md) — where shared build properties are defined
  - [Common MSBuild Targets](./build/MSBUILD_COMMON_TARGETS.md) — standard MSBuild target execution order
- [Versioning and Release Channels](./VERSIONING.md) — synchronized versions, staging, promotion, and local package policy
- [References](./REFERENCES.md) — external MSBuild and tooling references

The ApplicationModel gateway family includes the portable model and export contracts, the
plan-driven gateway lifecycle, and the hosting-free authenticated gateway control plane used by
`remote.Gateway(url)`. Local gateways publish its discovery metadata beneath
`.cohesion/<application>/control-plane.json`; platform exposure remains platform-owned.

## Landed package and area map

The layers are L1 foundation and SDK/tooling, L2 application runtime and composition, and
L3 service platforms. The [ApplicationModel design v3](../libraries/ApplicationModel/DESIGN.md)
connects the packages to the signed direction and records the build-out decisions.

| Family | Packages and responsibility |
|---|---|
| ApplicationModel | `Assimalign.Cohesion.ApplicationModel` (portable contracts), `.Gateway` (plan-driven lifecycle and Local realization), `.Gateway.InProcess` (composite members), `.Gateway.ControlPlane` (authenticated application control plane). |
| Hosting | `Assimalign.Cohesion.Hosting` (nestable lifecycle), `.Health`, `.Resources` (ambient runtime context), `.Telemetry` (resource log-export bootstrap and shutdown). |
| OpenTelemetry | `Assimalign.Cohesion.OpenTelemetry` is implemented by item 31b (`acc951aa`): `OtlpProtocol`, `OtlpSignal`, `OtlpExporterOptions`, `IOtlpLogExporter`, `OtlpLogRecord`, and `OtlpExporter`; OTLP/HTTP JSON logs, with traces, metrics, and protobuf deferred. |
| Resource declarative planes | All 18 areas deliver a guarded `<Area>.ApplicationModel` package: typed resource, planner, graph verbs, and the area's default-control-plane factory. They are NuGet-only. |
| Web hosting family | `Assimalign.Cohesion.Web.Hosting.Resources` supplies resource control-plane integration; `Assimalign.Cohesion.Web.Hosting.Health` adapts Hosting contributors onto the independent Web health model. |
| Orchestration clients | `SecretStore.Client`, `ConfigurationStore.Client`, `IdentityHub.Client`, and `Rezolvr.Client` support gateway-side protected mounts and commands. |

The seven areas with real service hosts are **Web, Database, ConfigurationStore, SecretStore,
IdentityHub, Rezolvr, and LogSpace**. The eleven generic hosts are **ApiManager, EmailHub,
EventHub, IoTHub, LoadBalancer, MediaHub, MessageHub, NatGateway, NotificationHub, Scheduler,
and VpnGateway**. LogSpace now provides the OTLP receiver, segment store, scoped-token
verification and paged query; its `.Telemetry` project remains an empty placeholder.

The orchestration clients above are distinct from the existing area clients:
`Database.Client`, `Database.{Sql,Documents,Graph,Blob,Cache,KeyValuePair}.Client`,
`MessageHub.Client`, and `NotificationHub.Client`.

## Executable acceptance samples

These are non-packable consumers with real `Program.cs` entry points:

- `samples/SdkSmoke/`: `SdkSmoke.Analyzer`, `SdkSmoke.App`, `SdkSmoke.Database`, and `SdkSmoke.Web`.
- `resources/Database/samples/Assimalign.Cohesion.Database.SampleHost`.
- `resources/Web/samples/Assimalign.Cohesion.Web.HttpsHost` and `Assimalign.Cohesion.Web.TestHost`.
- `resources/LogSpace/samples/Assimalign.Cohesion.LogSpace.SinkHost` (item 31b).

The [runtime contract](RUNTIME_CONTRACT.md), [Hosting.Telemetry design](../libraries/Hosting/Assimalign.Cohesion.Hosting.Telemetry/docs/DESIGN.md),
and [OpenTelemetry design](../libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/docs/DESIGN.md)
own the precise injection, protocol, and lifecycle contracts.
