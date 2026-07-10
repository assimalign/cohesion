# Libraries

The foundation libraries (L1) of the Cohesion framework. Everything under this folder is a building block that the services under `resources/` compose; libraries never depend on services. Each library follows the repo-wide project shape defined in the repo coding rules (`.claude/rules/`) — co-located `src/`, `tests/`, and `docs/` (`OVERVIEW.md`, `DESIGN.md`, `docs/Assembly/`) — and every assembly is NativeAOT-compatible.

| Area | Purpose |
| --- | --- |
| `Amqp` | AMQP protocol implementation and its connection bindings. |
| `ApplicationModel` | Application orchestration contracts and deployment gateways (the control plane). |
| `Cache` | Caching abstractions and the in-memory cache implementation. |
| `Configuration` | Configuration model plus providers (JSON, XML, INI, environment variables, command line, file system). |
| `Connections` | Connection contracts and transport drivers, including TLS via the Security area. |
| `Content` | Content and container formats (binary, BMFF, EBML/MKV, markdown, media, executables). |
| `Core` | Primitives and value types shared by every other library. |
| `DependencyInjection` | Dependency injection container and abstractions. |
| `Dns` | DNS protocol and resolver. |
| `FileSystem` | File-system abstractions, globbing, and virtual file systems. |
| `Hosting` | Service hosting model and lifecycle. |
| `Http` | HTTP protocol stack (HTTP/1.1, HTTP/2, HTTP/3/QPACK). |
| `IdentityModel` | Identity, claims, and token model. |
| `Logging` | Logging abstractions and sinks. |
| `ObjectMapping` | Object-to-object mapping. |
| `ObjectPool` | Object pooling. |
| `ObjectValidation` | Object validation rules and evaluation. |
| `OpenApi` | OpenAPI document model, reading, and writing. |
| `OpenTelemetry` | Telemetry and instrumentation. |
| `Resilience` | Resilience pipeline (retry, circuit breaking, timeouts). |
| `Security` | TLS, certificate management, and cryptographic helpers. |

For how these areas sequence into delivery waves and how L1 relates to the L2 (application runtime and composition) and L3 (service platform) layers, see [docs/DELIVERY_ROADMAP.md](../docs/DELIVERY_ROADMAP.md) and the dependency graph in the [repo README](../README.md).
