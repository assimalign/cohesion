# Overview

Repository-level documentation for the Cohesion mono repository. Coding standards and agent rules live in [`.claude/rules/`](../.claude/rules/) (auto-loaded by Claude Code; entry point [`.claude/CLAUDE.md`](../.claude/CLAUDE.md)).

**Go To**

- [Delivery Roadmap](./programs/DELIVERY_ROADMAP.md) — delivery waves, initiative sequencing, and the L1/L2/L3 layering model
- [Service Layer Design](./SERVICE_LAYER_DESIGN.md) — high-level design for each service under `resources/`
- [Service Story Requirements](./programs/SERVICE_STORY_REQUIREMENTS.md) — implementation requirements for service-level backlog stories
- [Developer Experience Design](./DEVELOPER_EXPERIENCE_DESIGN.md) — how a project picks a Cohesion SDK, the frameworks it receives, the canonical runtime API, and the hosting decisions in force
- [Runtime Contract](./RUNTIME_CONTRACT.md) — the language-neutral `COHESION_*` variables a gateway gives the instances it runs
- [Application Model design](./libraries/ApplicationModel/DESIGN.md) — gateways, managed resource instances, and the platforms between them
- Build
  - [Cohesion Custom MSBuild Items](./build/MSBUILD_COHESION_PROPS.md) — `CohesionProjectReference`, `CohesionPackageReference`, code generation
  - [Common MSBuild Properties](./build/MSBUILD_COMMON_PROPS.md) — where shared build properties are defined
  - [Common MSBuild Targets](./build/MSBUILD_COMMON_TARGETS.md) — standard MSBuild target execution order
- [Event Sources](./EVENT_SOURCES.md) — the internal `EventSource`s Cohesion libraries ship, and how to observe them with tracing tools or forward them into logging
- [Versioning and Release Channels](./VERSIONING_RELEASE_POLICY.md) — synchronized versions, staging, promotion, and local package policy
- [References](./REFERENCES.md) — external MSBuild and tooling references

## Landed package and area map

The layers are L1 foundation and SDK/tooling, L2 application runtime and composition, and
L3 service platforms.

| Family | Packages and responsibility |
|---|---|
| ApplicationModel | `Assimalign.Cohesion.ApplicationModel` (the shared identity), `.Protocol` (the gateway-to-instance wire), `.Definition` (the logical model a gateway runs), `.Hosting` (the running instance), `.Hosting.InProcess` (the in-process platform's instance half), `.Gateway` (deploys and manages one gateway's model through an infrastructure provider), `.Gateway.InProcess` (the in-process platform's gateway half). Rebuilt since 2026-10-27; see the [area README](../libraries/ApplicationModel/README.md). The Kubernetes and Docker platform pairs live in `cohesion-platforms`. |
| Hosting | `Assimalign.Cohesion.Hosting` (the nestable lifecycle host every area application is). |
| OpenTelemetry | `Assimalign.Cohesion.OpenTelemetry`: `OtlpProtocol`, `OtlpSignal`, `OtlpExporterOptions`, `IOtlpLogExporter`, `OtlpLogRecord`, and `OtlpExporter`; OTLP/HTTP JSON logs, with traces, metrics, and protobuf deferred. |
| Resource application-model integrations | Each area makes its application a managed resource with one package, `<Area>.Hosting.ApplicationModel`, a member of its `App.<Area>` framework: `builder.Services.AddResource(name, resource => resource.UseInProcess())`. Web's is the reference implementation ([Web.Hosting.ApplicationModel](../resources/Web/Assimalign.Cohesion.Web.Hosting.ApplicationModel/docs/DESIGN.md)). |
| Area clients | `Database.Client`, `Database.{Sql,Documents,Graph,Blob,Cache,KeyValuePair}.Client`, `ConfigurationStore.Client`, `SecretStore.Client`, `IdentityHub.Client`, `MessageHub.Client`, and `NotificationHub.Client` are ordinary client packages; an area framework carries one only when the area's own applications use it at run time. |

The seven areas with real service hosts are **Web, Database, ConfigurationStore, SecretStore,
IdentityHub, Rezolvr, and LogSpace**. The eleven generic hosts are **ApiManager, EmailHub,
EventHub, IoTHub, LoadBalancer, MediaHub, MessageHub, NatGateway, NotificationHub, Scheduler,
and VpnGateway**. LogSpace provides the OTLP receiver, segment store, scoped-token
verification and paged query; its `.Telemetry` project remains an empty placeholder.

## Executable acceptance samples

These are non-packable consumers with real `Program.cs` entry points:

- `fixtures/SdkSmoke/`: `SdkSmoke.Analyzer`, `SdkSmoke.App`, `SdkSmoke.Database`, and `SdkSmoke.Web`.
- `resources/Database/Assimalign.Cohesion.Database.Testing/fixtures/`: `Assimalign.Cohesion.Database.SampleHost` and `Assimalign.Cohesion.Database.TcpSdkHost`.

The [runtime contract](RUNTIME_CONTRACT.md) and the
[OpenTelemetry design](../libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/docs/DESIGN.md)
own the precise variable and protocol contracts.
